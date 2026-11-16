import copy
import json
from pathlib import Path
from unittest.mock import Mock

import pytest
import requests

from scripts import local_blockchain as module
from scripts.local_blockchain import Anvil, PLANS, atomic_json, main


TOKEN = "0x" + "1" * 40
CONTRACT = "0x" + "2" * 40
OWNER = "0x" + "3" * 40
TOKEN_TX = "0x" + "a" * 64
CONTRACT_TX = "0x" + "b" * 64


class Chain(Anvil):
    def __init__(self):
        super().__init__()
        self.height = 0
        self.nonce = 0
        self.sent = []
        self.codes = {}
        self.receipts = {}
        self.transactions = {}
        self.values = {"decimals()": 6, "paymentToken()": int(TOKEN, 16), "owner()": int(OWNER, 16), "paused()": 0, "prices(uint256)|1": 5_000_000, "prices(uint256)|2": 10_000_000}
        self.chain_id = "0x7a69"
        self.client = "anvil/v1.7.1"
        self.missing_block = False
        self.accounts = [OWNER]

    def rpc(self, method, params=None):
        params = params or []
        if method == "eth_chainId":
            return self.chain_id
        if method == "web3_clientVersion":
            return self.client
        if method == "eth_blockNumber":
            return hex(self.height)
        if method == "eth_accounts":
            return self.accounts
        if method == "eth_getTransactionCount":
            return hex(self.nonce)
        if method == "eth_getCode":
            return self.codes.get(params[0], "0x")
        if method == "eth_getTransactionReceipt":
            return self.receipts.get(params[0])
        if method == "eth_getTransactionByHash":
            return self.transactions.get(params[0])
        if method == "eth_getBlockByNumber":
            number = int(params[0], 16)
            return None if self.missing_block else {"number": hex(number), "hash": "0x" + f"{number + 1:064x}"}
        if method == "eth_call":
            return "0x" + f"{self.values[params[0]['data']]:064x}"
        if method == "eth_sendTransaction":
            self.sent.append(params[0])
            self.height += 1
            if params[0].get("to"):
                transaction = "0x" + "c" * 64
                self.receipts[transaction] = {"status": "0x1", "blockNumber": hex(self.height), "blockHash": self.block(self.height)["hash"]}
                self.transactions[transaction] = {"from": params[0]["from"], "to": params[0]["to"], "input": params[0]["data"]}
                return transaction
            target = TOKEN if len(self.sent) == 1 else CONTRACT
            transaction = TOKEN_TX if target == TOKEN else CONTRACT_TX
            self.codes[target] = self.expected_runtime("MockUSDC" if target == TOKEN else "BirdLabSubscriptions", TOKEN)
            self.receipts[transaction] = {"status": "0x1", "contractAddress": target, "blockNumber": hex(self.height), "blockHash": self.block(self.height)["hash"]}
            return transaction
        raise AssertionError(method)

    @staticmethod
    def calldata(signature, *args):
        return "|".join((signature, *map(str, args)))

    @staticmethod
    def code_hash(code):
        return "0x" + code[2:].rjust(64, "0")

    def expected_runtime(self, name, token):
        return "0x6001" if name == "MockUSDC" else "0x6002"

    @staticmethod
    def artifact(name):
        return {"bytecode": {"object": "0x6001" if name == "MockUSDC" else "0x6002"}}

    def call(self, sender, target, signature, *args):
        self.height += 1
        return "0x" + f"{self.height:064x}"


@pytest.fixture
def deployment(tmp_path):
    chain = Chain()
    path = tmp_path / "deployment.json"
    record = chain.deploy(deployment_path=path, build=False)
    return chain, path, record


def test_initialization_and_repeat_are_idempotent(deployment, monkeypatch):
    chain, path, record = deployment
    assert record["version"] == 1
    assert record["plans"] == PLANS
    assert record["checkpointBlockNumber"] == 5
    assert record["deploymentBlockNumber"] == 2
    assert len(chain.sent) == 3
    build = Mock()
    monkeypatch.setattr(module.subprocess, "run", build)
    assert chain.deploy(deployment_path=path) == record
    build.assert_called_once()
    assert len(chain.sent) == 3


def test_legacy_adoption_preserves_history_and_contracts(deployment, tmp_path):
    chain, _, record = deployment
    legacy = tmp_path / "legacy.json"
    legacy.write_text(json.dumps({key: value for key, value in record.items() if key not in {"version", "plans", "deploymentBlockNumber", "deploymentBlockHash", "checkpointBlockNumber", "checkpointBlockHash", "genesisBlockHash", "tokenRuntimeCodeHash", "paymentRuntimeCodeHash", "networkIdentityTransactionHash", "networkIdentityData"}}))
    target = tmp_path / "adopted.json"
    adopted = chain.deploy(deployment_path=target, legacy_path=legacy, build=False)
    assert adopted["tokenAddress"] == record["tokenAddress"]
    assert adopted["paymentContractAddress"] == record["paymentContractAddress"]
    assert adopted["checkpointBlockNumber"] > record["checkpointBlockNumber"]
    assert len([transaction for transaction in chain.sent if not transaction.get("to")]) == 2


@pytest.mark.parametrize("field,value,error", [
    ("chainId", 1, "metadata"), ("tokenDecimals", 18, "metadata"), ("testOnly", False, "metadata"), ("version", 2, "metadata"),
    ("tokenAddress", None, "address"), ("tokenAddress", "0x" + "z" * 40, "address"), ("tokenAddress", "0x" + "0" * 40, "address"),
    ("tokenAddress", CONTRACT, "differ"), ("tokenRuntimeCodeHash", "0x1", "hashes"), ("paymentRuntimeCodeHash", "0x2", "hashes"),
    ("tokenDeploymentTransactionHash", "bad", "identity"), ("deploymentTransactionHash", None, "identity"),
    ("deploymentBlockNumber", 99, "checkpoint"), ("deploymentBlockHash", "0x1", "checkpoint"),
    ("checkpointBlockNumber", "5", "checkpoint"), ("checkpointBlockHash", "0x1", "checkpoint"), ("plans", {}, "plan settings"), ("networkIdentityTransactionHash", None, "identity transaction"), ("networkIdentityData", "bad", "identity transaction"),
])
def test_incompatible_metadata_never_redeploys(deployment, field, value, error):
    chain, _, original = deployment
    record = {**original, field: value}
    with pytest.raises(RuntimeError, match=error):
        chain.verify(record)
    assert len(chain.sent) == 3


@pytest.mark.parametrize("scenario,error", [
    ("missing-token", "absent"), ("wrong-token", "bytecode"), ("wrong-payment", "bytecode"),
    ("decimals()", "decimals"), ("paymentToken()", "payment token"), ("owner()", "owner"), ("paused()", "paused"),
    ("prices(uint256)|1", "plan prices"), ("prices(uint256)|2", "plan prices"),
    ("missing-receipt", "receipt"), ("reverted-deployment", "receipt"), ("wrong-receipt-contract", "receipt"),
    ("missing-block", "history"), ("noncanonical-receipt", "canonical"),
])
def test_incompatible_network_never_redeploys(deployment, scenario, error):
    chain, _, record = deployment
    if scenario == "missing-token":
        del chain.codes[TOKEN]
    elif scenario in {"wrong-token", "wrong-payment"}:
        chain.codes[TOKEN if scenario == "wrong-token" else CONTRACT] = "0x6666"
    elif scenario in chain.values:
        chain.values[scenario] = 99
    elif scenario == "missing-receipt":
        del chain.receipts[TOKEN_TX]
    elif scenario == "reverted-deployment":
        chain.receipts[TOKEN_TX]["status"] = "0x0"
    elif scenario == "wrong-receipt-contract":
        chain.receipts[TOKEN_TX]["contractAddress"] = OWNER
    elif scenario == "missing-block":
        chain.missing_block = True
    else:
        chain.receipts[TOKEN_TX]["blockHash"] = "0x1"
    with pytest.raises(RuntimeError, match=error):
        chain.verify(record)
    assert len(chain.sent) == 3


@pytest.mark.parametrize("scenario", ["history", "nonpristine", "no-accounts", "guard-history", "guard-invalid", "guard-missing"])
def test_missing_manifest_does_not_replace_existing_network(tmp_path, scenario):
    chain = Chain()
    if scenario == "history":
        chain.height = 3
    if scenario == "nonpristine":
        chain.nonce = 1
    if scenario == "no-accounts":
        chain.accounts = []
    guard = tmp_path / "guard.json"
    if scenario in {"guard-history", "guard-invalid"}:
        guard.write_text(json.dumps({"hasLocalPaymentHistory": True} if scenario == "guard-history" else {}))
    with pytest.raises((RuntimeError, FileNotFoundError)):
        chain.deploy(deployment_path=tmp_path / "deployment.json", guard_path=guard if scenario.startswith("guard-") else None, build=False)
    assert not chain.sent


def test_pristine_sql_guard_allows_first_deploy(tmp_path):
    guard = tmp_path / "guard.json"
    atomic_json(guard, {"hasLocalPaymentHistory": False})
    assert Chain().deploy(deployment_path=tmp_path / "deployment.json", guard_path=guard, build=False)["version"] == 1


def test_incomplete_initialization_marker_blocks_automatic_retry(tmp_path, monkeypatch):
    chain = Chain()
    path = tmp_path / "deployment.json"
    monkeypatch.setattr(chain, "send", Mock(side_effect=RuntimeError("interrupted")))
    with pytest.raises(RuntimeError, match="interrupted"):
        chain.deploy(deployment_path=path, build=False)
    assert json.loads(path.read_text())["initializing"] is True
    with pytest.raises(RuntimeError, match="metadata"):
        chain.deploy(deployment_path=path, build=False)
    chain.send.assert_called_once()


@pytest.mark.parametrize("url,internal", [("http://example.com:8545", False), ("http://anvil:8545", False), ("http://anvil:9999", True), ("https://anvil:8545", True), ("ftp://localhost", False), ("http://user:password@localhost", False), ("http://localhost/path", False), ("http://localhost?q=1", False), ("http://localhost#fragment", False)])
def test_external_or_credentialed_rpc_is_rejected(url, internal):
    with pytest.raises(ValueError):
        Anvil(url, allow_internal=internal)


def test_only_explicit_docker_service_is_allowed():
    assert Anvil("http://anvil:8545", allow_internal=True).url == "http://anvil:8545"
    assert Anvil("https://[::1]:18545/").url.endswith("/")


@pytest.mark.parametrize("chain_id,client", [("0x1", "anvil"), ("0x7a69", "geth")])
def test_chain_id_and_client_are_required(chain_id, client):
    chain = Chain()
    chain.chain_id, chain.client = chain_id, client
    with pytest.raises(RuntimeError):
        chain.require_local()


def test_rpc_transport_and_error_details_are_not_exposed():
    chain = Anvil()
    response = Mock()
    response.json.return_value = {"result": "0x1"}
    chain.http = Mock()
    chain.http.post.return_value = response
    assert chain.rpc("eth_chainId") == "0x1"
    response.raise_for_status.assert_called_once()
    response.json.return_value = {"error": {"message": "private transport details"}}
    with pytest.raises(RuntimeError, match="Anvil RPC eth_chainId failed") as error:
        chain.rpc("eth_chainId")
    assert "private" not in str(error.value)


def test_wait_for_rpc_and_deadline(monkeypatch):
    chain = Anvil()
    monkeypatch.setattr(module.time, "sleep", Mock())
    chain.require_local = Mock(side_effect=[requests.ConnectionError(), None])
    chain.wait_ready()
    assert chain.require_local.call_count == 2
    chain.require_local = Mock(side_effect=requests.ConnectionError())
    monkeypatch.setattr(module.time, "monotonic", Mock(side_effect=[0, 2]))
    with pytest.raises(RuntimeError, match="deadline"):
        chain.wait_ready(seconds=1)


def test_receipt_pending_success_revert_and_timeout(monkeypatch):
    chain = Anvil()
    monkeypatch.setattr(module.time, "sleep", Mock())
    chain.rpc = Mock(side_effect=[None, {"status": "0x1"}])
    assert chain.receipt("0xa")["status"] == "0x1"
    chain.rpc = Mock(return_value={"status": "0x0"})
    with pytest.raises(RuntimeError, match="reverted"):
        chain.receipt("0xa")
    chain.rpc = Mock(return_value=None)
    with pytest.raises(RuntimeError, match="not mined"):
        chain.receipt("0xa")


def test_call_waits_and_calldata_and_hash_use_foundry(monkeypatch):
    monkeypatch.setattr(module.subprocess, "check_output", Mock(return_value="0x1234\n"))
    assert Anvil.calldata("x(uint256)", 1) == "0x1234"
    assert Anvil.code_hash("0x1111") == "0x1234"
    chain = Anvil()
    chain.send = Mock(return_value="0xa")
    chain.receipt = Mock()
    assert chain.call(OWNER, CONTRACT, "x(uint256)", 1) == "0xa"
    chain.receipt.assert_called_once_with("0xa")


def test_build_artifact_runtime_immutables_and_loading(tmp_path, monkeypatch):
    root = tmp_path / "contracts/out/MockUSDC.sol"
    root.mkdir(parents=True)
    (root / "MockUSDC.json").write_text(json.dumps({"deployedBytecode": {"object": "0x6001", "immutableReferences": {}}}))
    monkeypatch.setattr(module, "ROOT", tmp_path)
    chain = Anvil()
    assert chain.expected_runtime("MockUSDC", TOKEN) == "0x6001"
    artifact = {"deployedBytecode": {"object": "0x60" + "00" * 32 + "01", "immutableReferences": {"1": [{"start": 1, "length": 32}]}}}
    monkeypatch.setattr(chain, "artifact", lambda _: copy.deepcopy(artifact))
    assert chain.expected_runtime("BirdLabSubscriptions", TOKEN) == "0x60" + TOKEN[2:].rjust(64, "0") + "01"
    with pytest.raises(RuntimeError, match="layout"):
        chain.expected_runtime("MockUSDC", TOKEN)
    artifact["deployedBytecode"]["immutableReferences"]["1"][0]["length"] = 20
    with pytest.raises(RuntimeError, match="size"):
        chain.expected_runtime("BirdLabSubscriptions", TOKEN)
    artifact["deployedBytecode"]["immutableReferences"] = {}
    with pytest.raises(RuntimeError, match="layout"):
        chain.expected_runtime("BirdLabSubscriptions", TOKEN)


def test_main_success_and_failure_are_safe(monkeypatch, capsys):
    deploy = Mock(return_value={"chainId": 31337, "tokenAddress": TOKEN, "paymentContractAddress": CONTRACT})
    monkeypatch.setattr(Anvil, "deploy", deploy)
    assert main(["--no-build", "--docker-network", "--rpc", "http://anvil:8545"]) == 0
    assert json.loads(capsys.readouterr().out)["ready"] is True
    deploy.side_effect = RuntimeError("Original chain must be restored.")
    assert main([]) == 1
    assert "restored" in json.loads(capsys.readouterr().out)["error"]
    deploy.side_effect = KeyError("private detail")
    assert main([]) == 1
    assert "private" not in capsys.readouterr().out


@pytest.mark.parametrize("scenario", ["missing-receipt", "missing-tx", "reverted", "wrong-block-number", "wrong-block-hash", "wrong-from", "wrong-to", "wrong-input"])
def test_unique_network_marker_is_required(deployment, scenario):
    chain, _, record = deployment
    identity = record["networkIdentityTransactionHash"]
    if scenario == "missing-receipt":
        del chain.receipts[identity]
    elif scenario == "missing-tx":
        del chain.transactions[identity]
    elif scenario == "reverted":
        chain.receipts[identity]["status"] = "0x0"
    elif scenario == "wrong-block-number":
        chain.receipts[identity]["blockNumber"] = "0x1"
    elif scenario == "wrong-block-hash":
        chain.receipts[identity]["blockHash"] = "0x" + "d" * 64
    else:
        chain.transactions[identity][scenario.removeprefix("wrong-")] = "0x" + "d" * 40
    with pytest.raises(RuntimeError, match="unique network identity"):
        chain.verify(record)
    assert len(chain.sent) == 3


def sql_guard(chain):
    transaction = "0x" + "d" * 64
    number = 7
    block_hash = chain.block(number)["hash"]
    chain.receipts[transaction] = {"status": "0x1", "blockNumber": hex(number), "blockHash": block_hash, "to": CONTRACT}
    return {"hasLocalPaymentHistory": True, "ordersAddresses": [{"tokenAddress": TOKEN, "paymentContractAddress": CONTRACT}], "confirmedPayments": [{"transactionHash": transaction, "blockNumber": number, "blockHash": block_hash, "tokenAddress": TOKEN, "paymentContractAddress": CONTRACT}]}


def test_sql_history_is_verified_before_legacy_identity_transaction(deployment, tmp_path):
    chain, _, record = deployment
    legacy = tmp_path / "legacy.json"
    legacy.write_text(json.dumps(record))
    guard = tmp_path / "sql-guard.json"
    atomic_json(guard, sql_guard(chain))
    target = tmp_path / "adopted.json"
    adopted = chain.deploy(deployment_path=target, legacy_path=legacy, guard_path=guard, build=False)
    assert adopted["paymentContractAddress"] == CONTRACT
    assert len([transaction for transaction in chain.sent if not transaction.get("to")]) == 2
    chain.deploy(deployment_path=target, guard_path=guard, build=False)


@pytest.mark.parametrize("scenario,error", [
    ("invalid-flag", "guard"), ("missing-anchors", "without usable"), ("invalid-list", "without usable"),
    ("invalid-order", "different contracts"), ("different-token", "different contracts"), ("different-contract", "different contracts"),
    ("invalid-number", "canonical transaction anchors"), ("invalid-block-hash", "canonical transaction anchors"), ("invalid-transaction", "canonical transaction anchors"),
    ("missing-receipt", "confirmed SQL payment"), ("reverted", "confirmed SQL payment"), ("wrong-number", "confirmed SQL payment"),
    ("wrong-receipt-hash", "confirmed SQL payment"), ("wrong-block-hash", "confirmed SQL payment"), ("wrong-recipient", "confirmed SQL payment"),
])
def test_sql_mismatch_blocks_legacy_migration_before_network_mutation(deployment, tmp_path, scenario, error):
    chain, _, record = deployment
    guard = sql_guard(chain)
    payment = guard["confirmedPayments"][0]
    receipt = chain.receipts[payment["transactionHash"]]
    if scenario == "invalid-flag":
        guard["hasLocalPaymentHistory"] = "true"
    elif scenario == "missing-anchors":
        guard = {"hasLocalPaymentHistory": True}
    elif scenario == "invalid-list":
        guard["ordersAddresses"] = {}
    elif scenario == "invalid-order":
        guard["ordersAddresses"] = [None]
    elif scenario == "different-token":
        guard["ordersAddresses"][0]["tokenAddress"] = OWNER
    elif scenario == "different-contract":
        payment["paymentContractAddress"] = OWNER
    elif scenario == "invalid-number":
        payment["blockNumber"] = None
    elif scenario == "invalid-block-hash":
        payment["blockHash"] = None
    elif scenario == "invalid-transaction":
        payment["transactionHash"] = "bad"
    elif scenario == "missing-receipt":
        del chain.receipts[payment["transactionHash"]]
    elif scenario == "reverted":
        receipt["status"] = "0x0"
    elif scenario == "wrong-number":
        receipt["blockNumber"] = "0x1"
    elif scenario == "wrong-receipt-hash":
        receipt["blockHash"] = "0x" + "e" * 64
    elif scenario == "wrong-block-hash":
        payment["blockHash"] = "0x" + "e" * 64
    else:
        receipt["to"] = OWNER
    guard_path = tmp_path / "sql-guard.json"
    legacy_path = tmp_path / "legacy.json"
    atomic_json(guard_path, guard)
    atomic_json(legacy_path, record)
    with pytest.raises(RuntimeError, match=error):
        chain.deploy(deployment_path=tmp_path / "new.json", legacy_path=legacy_path, guard_path=guard_path, build=False)
    assert len(chain.sent) == 3
    assert not (tmp_path / "new.json").exists()


def test_interrupted_or_concurrent_initializer_lock_is_fail_closed(tmp_path):
    chain = Chain()
    target = tmp_path / "deployment.json"
    target.with_suffix(".json.lock").touch()
    with pytest.raises(RuntimeError, match="deployment lock"):
        chain.deploy(deployment_path=target, build=False)
    assert not chain.sent


def test_malformed_json_object_is_rejected_without_a_transaction(tmp_path):
    chain = Chain()
    target = tmp_path / "deployment.json"
    target.write_text("[]")
    with pytest.raises(RuntimeError, match="JSON object"):
        chain.deploy(deployment_path=target, build=False)
    assert not chain.sent
    assert not target.with_suffix(".json.lock").exists()
