import { afterEach, beforeEach, expect, it, vi } from "vitest";
import type { EIP1193Provider } from "viem";
import type { Network, PaymentOrder } from "../../shared/subscriptions";
import { approveOrder, connectWallet, discoverWallets, estimatePayment, payOrder, signWallet, switchNetwork, walletBalances, walletError } from "./web3";

const boundary = vi.hoisted(() => ({
  requestAddresses: vi.fn(), getAddresses: vi.fn(), walletChain: vi.fn(), rpcChain: vi.fn(),
  switchChain: vi.fn(), addChain: vi.fn(), signMessage: vi.fn(), writeContract: vi.fn(),
  readContract: vi.fn(), getBalance: vi.fn(), simulateContract: vi.fn(), waitForTransactionReceipt: vi.fn(),
  estimateContractGas: vi.fn(), getGasPrice: vi.fn(),
}));
vi.mock("viem", async (original) => ({
  ...await original<typeof import("viem")>(),
  createWalletClient: () => ({
    requestAddresses: boundary.requestAddresses, getAddresses: boundary.getAddresses, getChainId: boundary.walletChain,
    switchChain: boundary.switchChain, addChain: boundary.addChain, signMessage: boundary.signMessage, writeContract: boundary.writeContract,
  }),
  createPublicClient: () => ({
    getChainId: boundary.rpcChain, readContract: boundary.readContract, getBalance: boundary.getBalance,
    simulateContract: boundary.simulateContract, waitForTransactionReceipt: boundary.waitForTransactionReceipt,
    estimateContractGas: boundary.estimateContractGas, getGasPrice: boundary.getGasPrice,
  }),
}));

const address = "0x3333333333333333333333333333333333333333";
const hash = "0xaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
const network: Network = {
  enabled: true, testMode: true, chainId: 31337, rpcUrl: "http://127.0.0.1:8545",
  tokenAddress: "0x1111111111111111111111111111111111111111",
  paymentContractAddress: "0x2222222222222222222222222222222222222222",
  tokenDecimals: 6, confirmations: 2, explorerUrl: "",
};
const provider = { request: vi.fn() } as unknown as EIP1193Provider;
const order: PaymentOrder = {
  id: "fixture-order", orderId: "0x3333333333333333333333333333333333333333000000000000000000000001",
  walletAddress: address, chainId: network.chainId, tokenContractAddress: network.tokenAddress,
  paymentContractAddress: network.paymentContractAddress, planId: 1, amount: "5000000", tokenDecimals: 6,
  durationDays: 30, analysisLimit: 100, features: ["image"], status: "Created", replaceCurrentPlan: false,
  createdAt: "2026-10-30T12:00:00Z", expiresAt: "2026-10-30T12:30:00Z",
};

beforeEach(() => {
  vi.resetAllMocks();
  vi.spyOn(Date, "now").mockReturnValue(Date.parse(order.createdAt));
  boundary.requestAddresses.mockResolvedValue([address]);
  boundary.getAddresses.mockResolvedValue([address]);
  boundary.walletChain.mockResolvedValue(network.chainId);
  boundary.rpcChain.mockResolvedValue(network.chainId);
  boundary.readContract.mockResolvedValue(6_000_000n);
  boundary.getBalance.mockResolvedValue(10n ** 18n);
  boundary.simulateContract.mockImplementation(async (request) => ({ request }));
  boundary.waitForTransactionReceipt.mockResolvedValue({ status: "success" });
  boundary.writeContract.mockResolvedValue(hash);
});
afterEach(() => {
  vi.restoreAllMocks();
  delete window.ethereum;
});

it("discovers EIP-6963 and injected wallets, then removes the discovery listener", () => {
  window.ethereum = Object.assign({}, provider, { providers: [provider, provider] });
  const announce = vi.fn();
  const dispatch = () => window.dispatchEvent(new CustomEvent("eip6963:announceProvider", {
    detail: { info: { uuid: "wallet-a", name: "A".repeat(100) }, provider },
  }));
  window.addEventListener("eip6963:requestProvider", dispatch, { once: true });
  const stop = discoverWallets(announce);
  expect(announce.mock.calls.map(([wallet]) => wallet.id)).toEqual(["wallet-a", "injected-0", "injected-1"]);
  expect(announce.mock.calls[0][0].name).toHaveLength(80);
  window.dispatchEvent(new CustomEvent("eip6963:announceProvider", { detail: { provider: {} } }));
  stop();
  dispatch();
  expect(announce).toHaveBeenCalledTimes(3);
});

it("requests permission only on connect, refuses mainnet, and rejects an empty address list", async () => {
  await expect(connectWallet(provider, { ...network, chainId: 1 })).rejects.toThrow("лише Base Sepolia");
  expect(boundary.requestAddresses).not.toHaveBeenCalled();
  await expect(connectWallet(provider, network)).resolves.toEqual({ address, chainId: network.chainId });
  boundary.requestAddresses.mockResolvedValueOnce([]);
  await expect(connectWallet(provider, network)).rejects.toThrow("не надав адресу");
});

it("adds a missing chain and propagates wallet rejection", async () => {
  boundary.switchChain.mockRejectedValueOnce({ cause: { code: 4902 } });
  await expect(switchNetwork(provider, { ...network, chainId: 84532 })).resolves.toBe(network.chainId);
  expect(boundary.addChain).toHaveBeenCalledWith({ chain: expect.objectContaining({ id: 84532, name: "Base Sepolia" }) });
  expect(boundary.switchChain).toHaveBeenCalledTimes(2);
  boundary.switchChain.mockRejectedValueOnce({ code: 4001 });
  await expect(switchNetwork(provider, network)).rejects.toMatchObject({ code: 4001 });
  expect(boundary.addChain).toHaveBeenCalledTimes(1);
});

it.each(["address", "wallet chain", "RPC chain"])("stops signing after a change in %s", async (scenario) => {
  if (scenario === "address") boundary.getAddresses.mockResolvedValueOnce([network.tokenAddress]);
  if (scenario === "wallet chain") boundary.walletChain.mockResolvedValueOnce(1);
  if (scenario === "RPC chain") boundary.rpcChain.mockResolvedValueOnce(1);
  await expect(signWallet(provider, network, address, "nonce message")).rejects.toThrow("змінилася");
  expect(boundary.signMessage).not.toHaveBeenCalled();
});

it("signs exactly the SIWE text and uses both server-configured balances", async () => {
  boundary.signMessage.mockResolvedValue(hash);
  await expect(signWallet(provider, network, address, "nonce message")).resolves.toBe(hash);
  expect(boundary.signMessage).toHaveBeenCalledWith({ account: address, message: "nonce message" });
  await expect(walletBalances(provider, network, address)).resolves.toEqual({ usdc: 6_000_000n, gas: 10n ** 18n });
  expect(boundary.readContract).toHaveBeenCalledWith(expect.objectContaining({
    address: network.tokenAddress, functionName: "balanceOf", args: [address],
  }));
});

it.each(["token", "gas", "expired"])("blocks allowance approval when %s is invalid", async (scenario) => {
  if (scenario === "token") boundary.readContract.mockResolvedValueOnce(1n);
  if (scenario === "gas") boundary.getBalance.mockResolvedValueOnce(0n);
  if (scenario === "expired") vi.mocked(Date.now).mockReturnValue(Date.parse(order.expiresAt));
  await expect(approveOrder(provider, network, order)).rejects.toThrow(/USDC|ETH|Строк замовлення/);
  expect(boundary.simulateContract).not.toHaveBeenCalled();
  expect(boundary.writeContract).not.toHaveBeenCalled();
});

it("approves only the exact amount and fails if the approval receipt reverts", async () => {
  await expect(approveOrder(provider, network, order)).resolves.toBe(hash);
  expect(boundary.writeContract).toHaveBeenCalledWith(expect.objectContaining({
    account: address, address: network.tokenAddress, functionName: "approve",
    args: [network.paymentContractAddress, 5_000_000n],
  }));
  expect(boundary.waitForTransactionReceipt).toHaveBeenCalledWith({ hash, confirmations: 1, timeout: 120000 });
  boundary.waitForTransactionReceipt.mockResolvedValueOnce({ status: "reverted" });
  await expect(approveOrder(provider, network, order)).rejects.toThrow("Дозвіл ERC-20 не підтверджено");
});

it("estimates and submits the exact order call, then prevents payment after expiration", async () => {
  boundary.estimateContractGas.mockResolvedValue(21_000n);
  boundary.getGasPrice.mockResolvedValue(10n ** 9n);
  await expect(estimatePayment(provider, network, order)).resolves.toBe("0.000021");
  await expect(payOrder(provider, network, order)).resolves.toBe(hash);
  const purchase = expect.objectContaining({
    account: address, address: order.paymentContractAddress,
    functionName: "purchaseSubscription", args: [order.orderId, 1n],
  });
  expect(boundary.estimateContractGas).toHaveBeenCalledWith(purchase);
  expect(boundary.writeContract).toHaveBeenCalledWith(purchase);
  vi.mocked(Date.now).mockReturnValue(Date.parse(order.expiresAt));
  await expect(payOrder(provider, network, order)).rejects.toThrow("Не надсилайте платіж");
  expect(boundary.writeContract).toHaveBeenCalledTimes(1);
});

it.each([
  [{ code: 4001 }, "Дію скасовано"], [{ cause: { code: 4001 } }, "Дію скасовано"],
  [{ name: "UserRejectedRequestError" }, "Дію скасовано"], [new Error("User rejected"), "Дію скасовано"],
  [{ name: "InsufficientFundsError" }, "Недостатньо тестового ETH"],
  [new Error("Локальна помилка"), "Локальна помилка"], [new Error("Unreachable"), "Гаманець або RPC"],
])("maps wallet failure %j to a user-visible error", (error, text) => {
  expect(walletError(error)).toContain(text);
});
