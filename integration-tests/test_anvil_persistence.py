import json
import os
import subprocess
from pathlib import Path
from uuid import uuid4

import pytest

from scripts.local_blockchain import Anvil, ROOT


def docker(*arguments, env=None):
    return subprocess.check_output(["docker", *arguments], cwd=ROOT, env=env, text=True, encoding="utf-8", stderr=subprocess.STDOUT, timeout=180).strip()


@pytest.mark.parametrize("approved_transition", [False, True])
def test_compose_keeps_contract_state_receipts_and_payments_across_lifecycle(tmp_path, approved_transition):
    environment = os.environ.copy()
    environment.update({"MSSQL_SA_PASSWORD": "ComposeValidationOnly1!", "RABBITMQ_PASSWORD": "ComposeValidationOnly1!", "RABBITMQ_USERNAME": "birdlab-test", "AI_INTERNAL_KEY": "ComposeValidationOnly1!", "BLOCKCHAIN_NETWORK_INSTANCE_ID": "", "BLOCKCHAIN_TRANSITION_ID": ""})
    local = json.loads(docker("compose", "--env-file", ".env.example", "config", "--format", "json", env=environment))
    production = json.loads(docker("compose", "--env-file", ".env.example", "-f", "compose.yaml", "config", "--format", "json", env=environment))
    assert "anvil" not in production["services"]
    assert {"anvil", "blockchain-init", "blockchain-guard", "blockchain-bind"} <= local["services"].keys()
    assert "blockchain-init" not in local["services"]["central"]["depends_on"]
    assert local["services"]["blockchain-init"]["depends_on"]["anvil"]["condition"] == "service_healthy"
    assert local["services"]["blockchain-bind"]["depends_on"]["blockchain-init"]["condition"] == "service_completed_successfully"
    assert "blockchain-bind" not in local["services"]["central"]["depends_on"]
    service = local["services"]["anvil"]
    assert service["ports"][0]["host_ip"] == "127.0.0.1"
    assert "--preserve-historical-states" in service["command"]
    service["ports"] = [{"target": 8545, "host_ip": "127.0.0.1"}]
    project = "birdlab-chain-test-" + uuid4().hex[:10]
    configuration = tmp_path / "lifecycle.compose.json"
    config_directory = tmp_path / "config"
    legacy_directory = tmp_path / "legacy"
    config_directory.mkdir()
    legacy_directory.mkdir()
    network_instance, transition_id = str(uuid4()), str(uuid4())
    active = {"networkInstanceId": network_instance, "phase": "Planned", "chainId": 31337, "authorization": None, "ordersAddresses": [], "confirmedPayments": []}
    guard = {"version": 2, "hasLocalPaymentHistory": approved_transition, "activeNetwork": active, "archivedNetworks": []}
    if approved_transition:
        active["authorization"] = {"backupSha256": "b" * 64, "sourceFingerprint": "f" * 64, "legacyArchived": True, "transitionId": transition_id}
        guard["archivedNetworks"] = [{"networkInstanceId": str(uuid4()), "phase": "ArchivedUnverified", "chainId": 31337, "ordersAddresses": [{"tokenAddress": "0x" + "1" * 40, "paymentContractAddress": "0x" + "2" * 40}], "confirmedPayments": [{"transactionHash": "0x" + "f" * 64, "blockNumber": 700, "blockHash": "0x" + "e" * 64, "tokenAddress": "0x" + "1" * 40, "paymentContractAddress": "0x" + "2" * 40}]}]
        (legacy_directory / "local-deployment.json").write_text(json.dumps({"legacyDatasetMustRemainUnchanged": True}), encoding="utf-8")
    (config_directory / "sql-guard.json").write_text(json.dumps(guard), encoding="utf-8")
    initializer = local["services"]["blockchain-init"]
    initializer["depends_on"] = {"anvil": {"condition": "service_healthy"}}
    initializer["command"] = ["--rpc", "http://anvil:8545", "--docker-network", "--no-build", "--deployment", "/blockchain/deployment.json", "--guard", "/blockchain/sql-guard.json", "--legacy", "/legacy/local-deployment.json"]
    if approved_transition:
        initializer["command"] += ["--network-instance-id", network_instance, "--transition-id", transition_id]
    initializer["volumes"] = [{"type": "bind", "source": str(config_directory), "target": "/blockchain"}, {"type": "bind", "source": str(legacy_directory), "target": "/legacy", "read_only": True}]
    configuration.write_text(json.dumps({"services": {"anvil": service, "blockchain-init": initializer}, "volumes": {"anvil-state": {}}, "networks": {"default": {}}}), encoding="utf-8")

    def compose(*arguments):
        return docker("compose", "-p", project, "-f", str(configuration), *arguments)

    def connect():
        endpoint = "http://" + compose("port", "anvil", "8545")
        chain = Anvil(endpoint)
        chain.wait_ready()
        chain.calldata = lambda signature, *arguments: compose("exec", "-T", "anvil", "cast", "calldata", signature, *map(str, arguments))
        return chain

    try:
        compose("build", "blockchain-init")
        compose("up", "-d", "--wait", "--wait-timeout", "60", "anvil")
        assert json.loads(compose("run", "--rm", "--no-deps", "blockchain-init").splitlines()[-1])["ready"] is True
        chain = connect()
        deployment_path = config_directory / "deployment.json"
        deployment = json.loads(deployment_path.read_text(encoding="utf-8"))
        assert deployment["networkInstanceId"] == network_instance
        owner, payer = chain.rpc("eth_accounts")[:2]
        token = deployment["tokenAddress"]
        contract = deployment["paymentContractAddress"]
        order = payer + uuid4().hex[:24]
        chain.call(owner, token, "mint(address,uint256)", payer, 20_000_000)
        chain.call(payer, token, "approve(address,uint256)", contract, 15_000_000)
        purchase_data = chain.calldata("purchaseSubscription(bytes32,uint256)", order, 1)
        transaction = chain.send(payer, contract, purchase_data)
        purchase = chain.receipt(transaction)
        assert len(purchase["logs"]) == 2

        def state(node):
            return {
                "height": node.rpc("eth_blockNumber"),
                "eth": node.rpc("eth_getBalance", [payer, "latest"]),
                "payerUsdc": node.read(token, "balanceOf(address)", payer),
                "contractUsdc": node.read(token, "balanceOf(address)", contract),
                "allowance": node.read(token, "allowance(address,address)", payer, contract),
                "paidOrder": node.read(contract, "paidOrders(bytes32)", order),
                "receipt": node.rpc("eth_getTransactionReceipt", [transaction]),
                "transaction": node.rpc("eth_getTransactionByHash", [transaction]),
                "logs": node.rpc("eth_getLogs", [{"address": contract, "fromBlock": "0x0", "toBlock": "latest"}]),
                "code": [node.rpc("eth_getCode", [address, "latest"]) for address in (token, contract)],
                "block": node.block(int(purchase["blockNumber"], 16)),
            }

        expected = state(chain)
        guard["hasLocalPaymentHistory"] = True
        active.update({"phase": "Deployed", "deployment": deployment, "ordersAddresses": [{"tokenAddress": token, "paymentContractAddress": contract}], "confirmedPayments": [{"transactionHash": transaction, "blockNumber": int(purchase["blockNumber"], 16), "blockHash": purchase["blockHash"], "tokenAddress": token, "paymentContractAddress": contract}]})
        (config_directory / "sql-guard.json").write_text(json.dumps(guard), encoding="utf-8")
        assert int(expected["payerUsdc"], 16) == 15_000_000
        assert int(expected["contractUsdc"], 16) == 5_000_000
        assert int(expected["allowance"], 16) == 10_000_000
        for scenario in ("stop-start", "restart", "recreate", "down-up"):
            if scenario == "stop-start":
                compose("stop", "anvil")
                compose("start", "anvil", "--wait")
            elif scenario == "restart":
                compose("restart", "anvil")
            elif scenario == "recreate":
                compose("up", "-d", "--force-recreate", "--wait", "--wait-timeout", "60", "anvil")
            else:
                compose("down")
                compose("up", "-d", "--wait", "--wait-timeout", "60", "anvil")
            chain = connect()
            assert state(chain) == expected, scenario
            assert json.loads(deployment_path.read_text(encoding="utf-8")) == deployment
            assert json.loads(compose("run", "--rm", "--no-deps", "blockchain-init").splitlines()[-1])["ready"] is True
            assert chain.rpc("eth_blockNumber") == expected["height"]
        with pytest.raises(RuntimeError, match="reverted"):
            chain.receipt(chain.send(payer, contract, purchase_data))
        assert int(chain.read(token, "balanceOf(address)", payer), 16) == 15_000_000
        manifest_before = deployment_path.read_bytes()
        chain.rpc("anvil_reset")
        with pytest.raises(subprocess.CalledProcessError) as mismatch:
            compose("run", "--rm", "--no-deps", "blockchain-init")
        assert "Saved contracts are absent" in mismatch.value.output
        assert chain.rpc("eth_blockNumber") == "0x0"
        assert deployment_path.read_bytes() == manifest_before
    finally:
        compose("down")
