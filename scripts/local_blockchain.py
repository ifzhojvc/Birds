import argparse
import json
import os
import subprocess
import time
from pathlib import Path
from urllib.parse import urlparse
from uuid import UUID, uuid4

import requests


ROOT = Path(__file__).resolve().parents[1]
PLANS = {"1": 5_000_000, "2": 10_000_000}


def atomic_json(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_suffix(path.suffix + ".tmp")
    with temporary.open("w", encoding="utf-8") as output:
        json.dump(value, output, indent=2)
        output.flush()
        os.fsync(output.fileno())
    os.replace(temporary, path)


def read_metadata(path):
    value = json.loads(Path(path).read_text(encoding="utf-8"))
    if not isinstance(value, dict):
        raise RuntimeError("Local blockchain metadata must be a JSON object.")
    return value


def network_uuid(value):
    try:
        parsed = UUID(value)
        if parsed.int == 0:
            raise ValueError()
        return str(parsed)
    except (ValueError, TypeError, AttributeError):
        raise RuntimeError("The local network instance identity is missing or invalid.") from None


def digest(value):
    return isinstance(value, str) and len(value) == 64 and all(character in "0123456789abcdefABCDEF" for character in value)


class Anvil:
    def __init__(self, url="http://127.0.0.1:8545", *, allow_internal=False):
        endpoint = urlparse(url)
        local = endpoint.hostname in {"127.0.0.1", "localhost", "::1"}
        internal = allow_internal and endpoint.hostname == "anvil" and endpoint.port == 8545 and endpoint.scheme == "http"
        if not (local or internal) or endpoint.scheme not in {"http", "https"} or endpoint.username or endpoint.password or endpoint.query or endpoint.fragment or endpoint.path not in {"", "/"}:
            raise ValueError("Local integration accepts loopback Anvil or the explicitly enabled Docker anvil:8545 service only.")
        self.url = url
        self.http = requests.Session()

    def rpc(self, method, params=None):
        response = self.http.post(self.url, json={"jsonrpc": "2.0", "id": 1, "method": method, "params": params or []}, timeout=15)
        response.raise_for_status()
        payload = response.json()
        if "error" in payload:
            raise RuntimeError(f"Anvil RPC {method} failed.")
        return payload["result"]

    def require_local(self):
        if int(self.rpc("eth_chainId"), 16) != 31337:
            raise RuntimeError("Mainnet and external networks are forbidden by this script.")
        if "anvil" not in str(self.rpc("web3_clientVersion")).lower():
            raise RuntimeError("The local endpoint is not Anvil.")

    def wait_ready(self, seconds=90):
        deadline = time.monotonic() + seconds
        while True:
            try:
                self.require_local()
                return
            except requests.RequestException:
                if time.monotonic() >= deadline:
                    raise RuntimeError("Anvil RPC did not become ready before the deadline.") from None
                time.sleep(.5)

    def receipt(self, transaction):
        for _ in range(100):
            value = self.rpc("eth_getTransactionReceipt", [transaction])
            if value:
                if int(value["status"], 16) != 1:
                    raise RuntimeError("Local transaction reverted.")
                return value
            time.sleep(.1)
        raise RuntimeError("Local transaction was not mined.")

    def send(self, sender, to=None, data="0x"):
        self.require_local()
        transaction = {"from": sender, "data": data, "gas": hex(5_000_000)}
        if to:
            transaction["to"] = to
        return self.rpc("eth_sendTransaction", [transaction])

    @staticmethod
    def calldata(signature, *args):
        return subprocess.check_output(["cast", "calldata", signature, *map(str, args)], cwd=ROOT / "contracts", text=True, encoding="utf-8").strip()

    @staticmethod
    def code_hash(code):
        return subprocess.check_output(["cast", "keccak", code], cwd=ROOT / "contracts", text=True, encoding="utf-8").strip().lower()

    def call(self, sender, target, signature, *args):
        transaction = self.send(sender, target, self.calldata(signature, *args))
        self.receipt(transaction)
        return transaction

    def read(self, target, signature, *args):
        return self.rpc("eth_call", [{"to": target, "data": self.calldata(signature, *args)}, "latest"])

    @staticmethod
    def artifact(name):
        return json.loads((ROOT / f"contracts/out/{name}.sol/{name}.json").read_text(encoding="utf-8"))

    @staticmethod
    def address(value):
        if not isinstance(value, str) or len(value) != 42 or not value.startswith("0x"):
            raise RuntimeError("Deployment contains an invalid contract address.")
        try:
            if int(value[2:], 16) == 0:
                raise ValueError()
        except ValueError:
            raise RuntimeError("Deployment contains an invalid contract address.") from None
        return value.lower()

    def expected_runtime(self, name, token):
        artifact = self.artifact(name)["deployedBytecode"]
        code = artifact["object"].removeprefix("0x")
        references = artifact.get("immutableReferences", {})
        if name == "MockUSDC" and references or name == "BirdLabSubscriptions" and len(references) != 1:
            raise RuntimeError("Unexpected immutable layout in the contract build artifacts.")
        for entries in references.values():
            for entry in entries:
                start, length = entry["start"] * 2, entry["length"] * 2
                replacement = token[2:].rjust(length, "0")
                if length != 64:
                    raise RuntimeError("Unexpected immutable size in the contract build artifacts.")
                code = code[:start] + replacement + code[start + length:]
        return "0x" + code.lower()

    def block(self, number):
        value = self.rpc("eth_getBlockByNumber", [hex(number), False])
        if not value or not value.get("hash"):
            raise RuntimeError("The saved network history is missing. Payments remain unavailable; restore the original Anvil state.")
        return value

    def verify(self, record, *, legacy=False):
        self.require_local()
        if not legacy:
            network_uuid(record.get("networkInstanceId"))
        if record.get("chainId") != 31337 or record.get("tokenDecimals") != 6 or record.get("testOnly") is not True or not legacy and record.get("version") != 1:
            raise RuntimeError("Saved deployment metadata is incompatible with local payments.")
        token = self.address(record.get("tokenAddress"))
        contract = self.address(record.get("paymentContractAddress"))
        owner = self.address(record.get("ownerAddress"))
        if token == contract:
            raise RuntimeError("Token and payment contract addresses must differ.")
        hashes = {}
        for name, address, key in (("MockUSDC", token, "tokenRuntimeCodeHash"), ("BirdLabSubscriptions", contract, "paymentRuntimeCodeHash")):
            code = self.rpc("eth_getCode", [address, "latest"]).lower()
            if code in {"0x", "0x0", ""}:
                raise RuntimeError("Saved contracts are absent. Automatic redeployment is forbidden; restore the original Anvil state.")
            if code != self.expected_runtime(name, token):
                raise RuntimeError("Deployed bytecode differs from the expected contract version. Automatic redeployment is forbidden.")
            hashes[key] = self.code_hash(code)
            if not legacy and str(record.get(key, "")).lower() != hashes[key]:
                raise RuntimeError("Saved contract code hashes do not match this network.")
        if int(self.read(token, "decimals()"), 16) != 6 or self.address("0x" + self.read(contract, "paymentToken()")[-40:]) != token:
            raise RuntimeError("The payment token or token decimals is incompatible.")
        if self.address("0x" + self.read(contract, "owner()")[-40:]) != owner or int(self.read(contract, "paused()"), 16):
            raise RuntimeError("The payment owner or paused state is incompatible.")
        for plan, price in PLANS.items():
            if int(self.read(contract, "prices(uint256)", plan), 16) != price:
                raise RuntimeError("On-chain plan prices do not match local subscription prices.")
        deployment = None
        for key, address in (("tokenDeploymentTransactionHash", token), ("deploymentTransactionHash", contract)):
            transaction = record.get(key)
            if not isinstance(transaction, str) or len(transaction) != 66:
                raise RuntimeError("Saved deployment transaction identity is missing.")
            receipt = self.rpc("eth_getTransactionReceipt", [transaction])
            if not receipt or int(receipt.get("status", "0x0"), 16) != 1 or str(receipt.get("contractAddress", "")).lower() != address:
                raise RuntimeError("The original deployment receipt is missing or incompatible. Restore the original network; automatic redeployment is forbidden.")
            block = self.block(int(receipt["blockNumber"], 16))
            if block["hash"].lower() != receipt["blockHash"].lower():
                raise RuntimeError("Deployment is not part of the canonical saved network history.")
            if address == contract:
                deployment = receipt
        number = int(deployment["blockNumber"], 16)
        if not legacy and (record.get("deploymentBlockNumber") != number or str(record.get("deploymentBlockHash", "")).lower() != deployment["blockHash"].lower()):
            raise RuntimeError("Saved deployment checkpoint belongs to a different network history.")
        if not legacy:
            if record.get("plans") != PLANS:
                raise RuntimeError("Saved deployment plan settings are incompatible.")
            checkpoint = record.get("checkpointBlockNumber")
            if not isinstance(checkpoint, int) or self.block(checkpoint)["hash"].lower() != str(record.get("checkpointBlockHash", "")).lower():
                raise RuntimeError("Saved payment initialization checkpoint is missing or changed.")
            identity_hash = record.get("networkIdentityTransactionHash")
            identity_data = record.get("networkIdentityData")
            if not isinstance(identity_hash, str) or len(identity_hash) != 66 or not isinstance(identity_data, str) or len(identity_data) != 66:
                raise RuntimeError("The unique network identity transaction is missing.")
            identity_receipt = self.rpc("eth_getTransactionReceipt", [identity_hash])
            identity_transaction = self.rpc("eth_getTransactionByHash", [identity_hash])
            if not identity_receipt or not identity_transaction or int(identity_receipt.get("status", "0x0"), 16) != 1 or int(identity_receipt["blockNumber"], 16) != checkpoint or identity_receipt["blockHash"].lower() != record["checkpointBlockHash"].lower():
                raise RuntimeError("The unique network identity receipt is absent or belongs to another chain.")
            if str(identity_transaction.get("from", "")).lower() != owner or str(identity_transaction.get("to", "")).lower() != owner or str(identity_transaction.get("input", "")).lower() != identity_data.lower():
                raise RuntimeError("The unique network identity transaction does not match saved initialization.")
            return record
        checkpoint = self.block(int(self.rpc("eth_blockNumber"), 16))
        return {**record, **hashes, "version": 1, "deploymentBlockNumber": number, "deploymentBlockHash": deployment["blockHash"].lower(), "genesisBlockHash": self.block(0)["hash"].lower(), "checkpointBlockNumber": int(checkpoint["number"], 16), "checkpointBlockHash": checkpoint["hash"].lower(), "plans": PLANS}

    def anchor_identity(self, record):
        identity_data = "0x" + os.urandom(32).hex()
        transaction = self.send(record["ownerAddress"], record["ownerAddress"], identity_data)
        receipt = self.receipt(transaction)
        return {**record, "networkIdentityTransactionHash": transaction, "networkIdentityData": identity_data, "checkpointBlockNumber": int(receipt["blockNumber"], 16), "checkpointBlockHash": receipt["blockHash"].lower()}

    def verify_sql_history(self, record, guard):
        if not isinstance(guard.get("hasLocalPaymentHistory"), bool):
            raise RuntimeError("The SQL payment history guard is invalid.")
        orders = guard.get("ordersAddresses", [])
        payments = guard.get("confirmedPayments", [])
        if not isinstance(orders, list) or not isinstance(payments, list) or guard["hasLocalPaymentHistory"] and not orders and not payments:
            raise RuntimeError("SQL payment history exists without usable network anchors. Restore the original network before initialization.")
        for entry in orders + payments:
            if not isinstance(entry, dict) or self.address(entry.get("tokenAddress")) != record["tokenAddress"].lower() or self.address(entry.get("paymentContractAddress")) != record["paymentContractAddress"].lower():
                raise RuntimeError("SQL payment history refers to different contracts. Automatic network replacement is forbidden.")
        for payment in payments:
            number = payment.get("blockNumber")
            block_hash = payment.get("blockHash")
            transaction = payment.get("transactionHash")
            if not isinstance(number, int) or number < 1 or not isinstance(block_hash, str) or len(block_hash) != 66 or not isinstance(transaction, str) or len(transaction) != 66:
                raise RuntimeError("SQL confirmed payment is missing canonical transaction anchors.")
            receipt = self.rpc("eth_getTransactionReceipt", [transaction])
            if self.block(number)["hash"].lower() != block_hash.lower() or not receipt or int(receipt.get("status", "0x0"), 16) != 1 or int(receipt.get("blockNumber", "0x0"), 16) != number or str(receipt.get("blockHash", "")).lower() != block_hash.lower() or str(receipt.get("to", "")).lower() != record["paymentContractAddress"].lower():
                raise RuntimeError("A confirmed SQL payment is missing from this network. Restore the original Anvil state; initialization is blocked.")

    @staticmethod
    def guard_scope(guard, network_instance_id=None, transition_id=None):
        if guard is None or guard.get("version", 1) == 1:
            if guard is not None and "activeNetwork" in guard:
                raise RuntimeError("A scoped SQL network requires guard version 2.")
            if network_instance_id or transition_id:
                raise RuntimeError("Explicit network transitions require an approved version 2 SQL guard.")
            return None
        if guard.get("version") != 2 or not isinstance(guard.get("hasLocalPaymentHistory"), bool):
            raise RuntimeError("The SQL network guard version or history marker is invalid.")
        active = guard.get("activeNetwork")
        archives = guard.get("archivedNetworks")
        if not isinstance(active, dict) or not isinstance(archives, list):
            raise RuntimeError("The SQL network guard has no active network instance.")
        identity = network_uuid(active.get("networkInstanceId"))
        if network_instance_id and network_uuid(network_instance_id) != identity:
            raise RuntimeError("The requested network instance differs from the approved SQL instance.")
        if active.get("chainId") != 31337 or active.get("phase") not in {"Planned", "Deployed"}:
            raise RuntimeError("The SQL network instance phase or chain is incompatible.")
        orders, payments = active.get("ordersAddresses"), active.get("confirmedPayments")
        if not isinstance(orders, list) or not isinstance(payments, list):
            raise RuntimeError("The active SQL network history is malformed.")
        if active["phase"] == "Planned" and (orders or payments or active.get("deployment")):
            raise RuntimeError("A planned SQL network already contains payment evidence. Automatic deployment is forbidden.")
        archived_ids = set()
        for archive in archives:
            if not isinstance(archive, dict) or archive.get("phase") != "ArchivedUnverified" or archive.get("chainId") != 31337:
                raise RuntimeError("Archived network evidence is missing or incompatible.")
            archived_id = network_uuid(archive.get("networkInstanceId"))
            if archived_id == identity or archived_id in archived_ids:
                raise RuntimeError("Archived and active network identities must remain distinct.")
            archived_ids.add(archived_id)
            archived_orders, archived_payments = archive.get("ordersAddresses"), archive.get("confirmedPayments")
            if not isinstance(archived_orders, list) or not isinstance(archived_payments, list) or not archived_orders and not archived_payments:
                raise RuntimeError("Archived network payment anchors must be retained.")
        if not guard["hasLocalPaymentHistory"] and (archives or orders or payments):
            raise RuntimeError("SQL history cannot be marked empty while network evidence exists.")
        authorization = active.get("authorization")
        if archives:
            if not isinstance(authorization, dict) or authorization.get("legacyArchived") is not True or not digest(authorization.get("backupSha256")) or not digest(authorization.get("sourceFingerprint")):
                raise RuntimeError("The SQL transition has no verified backup and archival authorization.")
            approved_transition = network_uuid(authorization.get("transitionId"))
            if transition_id and network_uuid(transition_id) != approved_transition:
                raise RuntimeError("The requested transition differs from the approved SQL transition.")
            if active["phase"] == "Planned" and (not network_instance_id or not transition_id):
                raise RuntimeError("A legacy transition requires explicit matching network-instance-id and transition-id approval.")
        elif authorization is not None or transition_id or guard["hasLocalPaymentHistory"] and (active["phase"] == "Planned" or not orders and not payments):
            raise RuntimeError("Historical SQL payments require retained archived evidence and explicit transition approval.")
        if active["phase"] == "Deployed":
            if not isinstance(active.get("deployment"), dict):
                raise RuntimeError("The deployed SQL network has no bound contract manifest.")
            if network_uuid(active["deployment"].get("networkInstanceId")) != identity:
                raise RuntimeError("The bound deployment belongs to a different SQL network instance.")
        return active

    def verify_scoped_history(self, record, guard, scope):
        if scope is None:
            self.verify_sql_history(record, guard)
            return
        if network_uuid(record.get("networkInstanceId")) != network_uuid(scope["networkInstanceId"]):
            raise RuntimeError("Saved contracts belong to another SQL network instance. Automatic replacement is forbidden.")
        bound = scope.get("deployment")
        if bound:
            keys = ("networkInstanceId", "tokenAddress", "paymentContractAddress", "ownerAddress", "tokenDeploymentTransactionHash", "deploymentTransactionHash", "deploymentBlockNumber", "deploymentBlockHash", "networkIdentityTransactionHash", "networkIdentityData", "checkpointBlockNumber", "checkpointBlockHash", "tokenRuntimeCodeHash", "paymentRuntimeCodeHash")
            if any(str(bound.get(key)).lower() != str(record.get(key)).lower() for key in keys):
                raise RuntimeError("The manifest differs from the deployed SQL network binding.")
        current = {"hasLocalPaymentHistory": bool(scope["ordersAddresses"] or scope["confirmedPayments"]), "ordersAddresses": scope["ordersAddresses"], "confirmedPayments": scope["confirmedPayments"]}
        self.verify_sql_history(record, current)

    def deploy(self, *, deployment_path=ROOT / "verification/local-deployment.json", legacy_path=None, guard_path=None, build=True, network_instance_id=None, transition_id=None):
        deployment_path = Path(deployment_path)
        deployment_path.parent.mkdir(parents=True, exist_ok=True)
        lock = deployment_path.with_suffix(deployment_path.suffix + ".lock")
        try:
            descriptor = os.open(lock, os.O_CREAT | os.O_EXCL | os.O_WRONLY, 0o600)
        except FileExistsError:
            raise RuntimeError("Another or interrupted blockchain initializer owns the deployment lock. Inspect the original state before retrying.") from None
        os.close(descriptor)
        try:
            return self.initialize(deployment_path=deployment_path, legacy_path=legacy_path, guard_path=guard_path, build=build, network_instance_id=network_instance_id, transition_id=transition_id)
        finally:
            lock.unlink()

    def initialize(self, *, deployment_path=ROOT / "verification/local-deployment.json", legacy_path=None, guard_path=None, build=True, network_instance_id=None, transition_id=None):
        guard = read_metadata(guard_path) if guard_path else None
        scope = self.guard_scope(guard, network_instance_id, transition_id)
        self.wait_ready()
        if build:
            subprocess.run(["forge", "build", "--offline"], cwd=ROOT / "contracts", check=True, capture_output=True)
        deployment_path = Path(deployment_path)
        if deployment_path.exists():
            record = self.verify(read_metadata(deployment_path))
            if guard is not None:
                self.verify_scoped_history(record, guard, scope)
            return record
        if scope is not None and scope["phase"] != "Planned":
            raise RuntimeError("The SQL network was already deployed, but its manifest is missing. Restore that network; redeployment is forbidden.")
        if legacy_path and Path(legacy_path).exists():
            if scope is None:
                record = self.verify(read_metadata(legacy_path), legacy=True)
                if guard is not None:
                    self.verify_sql_history(record, guard)
                record["networkInstanceId"] = network_uuid(record["networkInstanceId"]) if record.get("networkInstanceId") else str(uuid4())
                atomic_json(deployment_path, {"initializing": True, "chainId": 31337, "networkInstanceId": record["networkInstanceId"]})
                record = self.anchor_identity(record)
                atomic_json(deployment_path, record)
                return record
            if not guard["archivedNetworks"]:
                raise RuntimeError("Legacy deployment metadata requires an approved archival transition; empty SQL history cannot replace it.")
        if int(self.rpc("eth_blockNumber"), 16) != 0:
            raise RuntimeError("Anvil already has history but no saved deployment. Automatic deployment is forbidden.")
        if guard is not None and scope is None and guard.get("hasLocalPaymentHistory") is not False:
            raise RuntimeError("SQL contains local payment history or the SQL guard is invalid. Restore the original Anvil state; fresh deployment is forbidden.")
        accounts = self.rpc("eth_accounts")
        if not accounts or any(int(self.rpc("eth_getTransactionCount", [account, "latest"]), 16) for account in accounts):
            raise RuntimeError("Anvil accounts are not pristine. Automatic deployment is forbidden.")
        identity = network_uuid(scope["networkInstanceId"]) if scope is not None else str(uuid4())
        owner = accounts[0]
        atomic_json(deployment_path, {"initializing": True, "chainId": 31337, "networkInstanceId": identity})
        token_hash = self.send(owner, data=self.artifact("MockUSDC")["bytecode"]["object"])
        token = self.receipt(token_hash)["contractAddress"]
        bytecode = self.artifact("BirdLabSubscriptions")["bytecode"]["object"]
        constructor = token[2:].rjust(64, "0") + owner[2:].rjust(64, "0")
        deploy_hash = self.send(owner, data=bytecode + constructor)
        contract = self.receipt(deploy_hash)["contractAddress"]
        for plan, price in PLANS.items():
            self.call(owner, contract, "setPlanPrice(uint256,uint256)", plan, price)
        record = {"networkInstanceId": identity, "chainId": 31337, "rpcUrl": self.url, "tokenAddress": token, "paymentContractAddress": contract, "ownerAddress": owner, "tokenDecimals": 6, "deploymentTransactionHash": deploy_hash, "tokenDeploymentTransactionHash": token_hash, "testOnly": True}
        if scope is not None and scope.get("authorization"):
            record.update({"transitionId": scope["authorization"]["transitionId"], "sourceBackupSha256": scope["authorization"]["backupSha256"], "sourceFingerprint": scope["authorization"]["sourceFingerprint"]})
        result = self.anchor_identity(self.verify(record, legacy=True))
        atomic_json(deployment_path, result)
        return result


def main(argv=None):
    parser = argparse.ArgumentParser(description="Idempotent local Anvil initialization without private keys.")
    parser.add_argument("--rpc", default="http://127.0.0.1:8545")
    parser.add_argument("--deployment", type=Path, default=ROOT / "verification/local-deployment.json")
    parser.add_argument("--legacy", type=Path)
    parser.add_argument("--guard", type=Path)
    parser.add_argument("--network-instance-id")
    parser.add_argument("--transition-id")
    parser.add_argument("--docker-network", action="store_true")
    parser.add_argument("--no-build", action="store_true")
    args = parser.parse_args(argv)
    try:
        result = Anvil(args.rpc, allow_internal=args.docker_network).deploy(deployment_path=args.deployment, legacy_path=args.legacy, guard_path=args.guard, build=not args.no_build, network_instance_id=args.network_instance_id, transition_id=args.transition_id)
        print(json.dumps({"ready": True, "networkInstanceId": result.get("networkInstanceId"), "chainId": result["chainId"], "tokenAddress": result["tokenAddress"], "paymentContractAddress": result["paymentContractAddress"]}))
        return 0
    except (ValueError, KeyError, TypeError, RuntimeError, OSError, subprocess.SubprocessError, requests.RequestException) as error:
        print(json.dumps({"ready": False, "error": str(error) if isinstance(error, RuntimeError) else "Local blockchain initialization failed; inspect deployment metadata, RPC availability and contract build artifacts."}))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
