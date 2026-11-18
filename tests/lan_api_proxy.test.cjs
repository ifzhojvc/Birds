const test = require("node:test");
const assert = require("node:assert/strict");
const { EventEmitter } = require("node:events");
const proxy = require("../scripts/birdlab-lan-api-proxy.cjs");

for (const [address, subnet] of [
  ["192.168.8.1", "192.168.8.0/24"],
  ["192.168.8.254", "192.168.8.0/24"],
  ["::ffff:192.168.8.10", "192.168.8.0/24"],
  ["::FFFF:c0a8:080a", "192.168.8.0/24"],
  ["10.23.42.8", "10.0.0.0/8"],
  ["172.16.1.10", "172.16.0.0/12"],
  ["192.168.8.10", "192.168.8.10/31"],
  ["192.168.8.11", "192.168.8.10/31"],
  ["192.168.8.10", "192.168.8.10/32"]
]) {
  test("accepts trusted source " + address + " in " + subnet, () => assert.equal(proxy.isTrustedAddress(address, subnet), true));
}

for (const address of [
  "8.8.8.8", "192.168.9.10", "10.0.0.1", "127.0.0.1", "::1", "::ffff:8.8.8.8",
  "2001:db8::1", "::ffff:c0a8:090a", "192.168.8.0", "192.168.8.255",
  "192.168.8.999", "192.168.08.10", "192.168.8.10.evil", "", null, undefined
]) {
  test("rejects source without opening upstream: " + address, () => {
    assert.equal(proxy.isTrustedAddress(address, "192.168.8.0/24"), false);
    let upstreams = 0;
    let destroyed = 0;
    const config = proxy.validateConfig({ bindAddress: "192.168.8.10", trustedSubnet: "192.168.8.0/24" });
    const handler = proxy.createConnectionHandler(config, () => { upstreams++; throw new Error("unexpected upstream"); });
    handler({ remoteAddress: address, headers: { "x-forwarded-for": "192.168.8.10" }, destroy() { destroyed++; } });
    assert.equal(upstreams, 0);
    assert.equal(destroyed, 1);
  });
}

for (const subnet of ["192.168.8.10/24", "192.168.8.0", "192.168.8.0/33", "192.168.8.0/-1", "192.168.8.0/024", "2001:db8::/32", "", null]) {
  test("rejects malformed or noncanonical subnet " + subnet, () => assert.throws(() => proxy.parseSubnet(subnet)));
}

test("validates /0 arithmetic independently of private LAN policy", () => {
  assert.deepEqual(proxy.parseSubnet("0.0.0.0/0"), { cidr: "0.0.0.0/0", prefix: 0, networkNumber: 0, broadcastNumber: 4294967295 });
});

for (const options of [
  { bindAddress: "0.0.0.0", trustedSubnet: "0.0.0.0/0" },
  { bindAddress: "::", trustedSubnet: "192.168.8.0/24" },
  { bindAddress: "8.8.8.8", trustedSubnet: "8.8.8.0/24" },
  { bindAddress: "127.0.0.1", trustedSubnet: "127.0.0.0/8" },
  { bindAddress: "192.168.8.10", trustedSubnet: "192.0.0.0/8" },
  { bindAddress: "172.31.1.10", trustedSubnet: "172.0.0.0/8" },
  { bindAddress: "192.168.9.10", trustedSubnet: "192.168.8.0/24" },
  { bindAddress: "192.168.8.0", trustedSubnet: "192.168.8.0/24" },
  { bindAddress: "192.168.8.255", trustedSubnet: "192.168.8.0/24" }
]) {
  test("refuses unsafe bind or subnet " + JSON.stringify(options), () => assert.throws(() => proxy.validateConfig(options)));
}

for (const port of [0, -1, 65536, "5100x", "1.5", "05100", null]) {
  test("rejects invalid port " + port, () => {
    const base = { bindAddress: "192.168.8.10", trustedSubnet: "192.168.8.0/24" };
    assert.throws(() => proxy.validateConfig({ ...base, port }));
    assert.throws(() => proxy.validateConfig({ ...base, upstreamPort: port }));
  });
}

test("validates another PC address and independent upstream port", () => {
  const config = proxy.validateConfig({ bindAddress: "10.42.0.20", trustedSubnet: "10.42.0.0/16", port: "55100", upstreamPort: "5100" });
  assert.equal(config.bindAddress, "10.42.0.20");
  assert.equal(config.port, 55100);
  assert.equal(config.upstreamPort, 5100);
});

test("requires explicit manual mode and rejects unknown or duplicate options", () => {
  const args = ["--bind", "192.168.8.10", "--trusted-subnet", "192.168.8.0/24"];
  assert.throws(() => proxy.parseArguments(args));
  assert.throws(() => proxy.parseArguments([...args, "--manual", "--automatic"]));
  assert.throws(() => proxy.parseArguments([...args, "--manual", "--bind", "192.168.8.11"]));
  assert.throws(() => proxy.parseArguments([...args, "--manual", "--port"]));
  assert.throws(() => proxy.parseArguments([...args, "--manual", "--check-config"]));
  assert.equal(proxy.parseArguments([...args, "--manual"]).checkOnly, false);
});

test("validation mode can resolve the subnet before an interface is selected", () => {
  const parsed = proxy.parseArguments(["--trusted-subnet", "10.42.0.0/16", "--check-config"]);
  assert.equal(parsed.checkOnly, true);
  assert.equal(parsed.config.bindAddress, null);
  assert.equal(parsed.config.subnet.networkNumber, 170524672);
});

class FakeSocket extends EventEmitter {
  constructor(address) { super(); this.remoteAddress = address; this.destroyedCount = 0; this.pipes = []; }
  destroy() { this.destroyedCount++; }
  setTimeout(value, callback) { this.timeout = value; this.timeoutCallback = callback; }
  pipe(destination) { this.pipes.push(destination); return destination; }
}

test("accepted sockets retain duplex transport and close both sides safely", () => {
  const config = proxy.validateConfig({ bindAddress: "192.168.8.10", trustedSubnet: "192.168.8.0/24" });
  const client = new FakeSocket("::ffff:192.168.8.42");
  const upstream = new FakeSocket("127.0.0.1");
  let calls = 0;
  proxy.createConnectionHandler(config, () => { calls++; return upstream; })(client);
  assert.equal(calls, 1);
  assert.deepEqual(client.pipes, [upstream]);
  assert.deepEqual(upstream.pipes, [client]);
  assert.equal(client.timeout, 300000);
  assert.equal(upstream.timeout, 300000);
  upstream.emit("error", new Error("upstream unavailable"));
  assert.equal(client.destroyedCount, 1);
  client.emit("error", new Error("client disconnected"));
  assert.equal(upstream.destroyedCount, 1);
  client.emit("close");
  upstream.emit("close");
  assert.equal(client.destroyedCount, 2);
  assert.equal(upstream.destroyedCount, 2);
  client.timeoutCallback();
  upstream.timeoutCallback();
  assert.equal(client.destroyedCount, 3);
  assert.equal(upstream.destroyedCount, 3);
});

test("an upstream connection failure closes only the accepted client", () => {
  const config = proxy.validateConfig({ bindAddress: "192.168.8.10", trustedSubnet: "192.168.8.0/24" });
  const client = new FakeSocket("192.168.8.42");
  proxy.createConnectionHandler(config, () => { throw new Error("connect failed"); })(client);
  assert.equal(client.destroyedCount, 1);
});
