import os
import subprocess
import time
from urllib.parse import urlparse
from uuid import uuid4

from scripts.local_blockchain import Anvil


def test_actual_contract_records_local_payment_once(tmp_path):
    endpoint = os.environ["BIRDLAB_TEST_ANVIL"]
    assert urlparse(endpoint).port not in (None, 8545), "Use a dedicated, disposable Anvil endpoint."
    chain = Anvil(endpoint)
    chain.require_local()
    deployment = chain.deploy(deployment_path=tmp_path / "local-deployment.json")
    owner, payer = chain.rpc("eth_accounts")[:2]
    token = deployment["tokenAddress"]
    contract = deployment["paymentContractAddress"]
    price = 5_000_000
    order = payer + uuid4().hex[:24]

    def read(signature, *arguments):
        return chain.rpc("eth_call", [{"to": token, "data": chain.calldata(signature, *arguments)}, "latest"])

    chain.call(owner, token, "mint(address,uint256)", payer, price * 2)
    chain.call(payer, token, "approve(address,uint256)", contract, price * 2)
    before = int(read("balanceOf(address)", payer), 16)
    purchase = chain.calldata("purchaseSubscription(bytes32,uint256)", order, 1)
    first = chain.receipt(chain.send(payer, contract, purchase))
    topic = subprocess.check_output(
        ["cast", "keccak", "SubscriptionPurchased(bytes32,address,uint256,uint256,address)"],
        text=True, encoding="utf-8",
    ).strip()
    events = [event for event in first["logs"] if event["address"].lower() == contract.lower() and event["topics"][0] == topic]
    assert len(events) == 1
    assert events[0]["topics"][1].lower() == order.lower()
    assert int(events[0]["data"][2:66], 16) == price
    assert events[0]["data"][-40:].lower() == token[2:].lower()
    repeat = chain.send(payer, contract, purchase)
    deadline = time.monotonic() + 10
    repeat_receipt = None
    while repeat_receipt is None and time.monotonic() < deadline:
        repeat_receipt = chain.rpc("eth_getTransactionReceipt", [repeat])
        if repeat_receipt is None:
            time.sleep(0.1)
    assert repeat_receipt is not None, "Repeat transaction was not mined."
    assert int(repeat_receipt["status"], 16) == 0
    assert int(read("balanceOf(address)", payer), 16) == before - price
    assert int(read("balanceOf(address)", contract), 16) == price
