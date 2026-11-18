const net = require("node:net");

function ipv4Number(address) {
  if (typeof address !== "string" || net.isIP(address) !== 4) throw new Error("An IPv4 address is required.");
  return address.split(".").reduce((value, part) => value * 256 + Number(part), 0);
}

function normalizeAddress(address) {
  if (typeof address !== "string") return null;
  if (net.isIP(address) === 4) return address;
  if (net.isIP(address) !== 6) return null;
  const dotted = /^::ffff:(\d+\.\d+\.\d+\.\d+)$/i.exec(address);
  if (dotted && net.isIP(dotted[1]) === 4) return dotted[1];
  const hex = /^::ffff:([0-9a-f]{1,4}):([0-9a-f]{1,4})$/i.exec(address);
  if (!hex) return null;
  const first = parseInt(hex[1], 16);
  const last = parseInt(hex[2], 16);
  return [first >>> 8, first & 255, last >>> 8, last & 255].join(".");
}

function parseSubnet(value) {
  const match = typeof value === "string" && /^([^/]+)\/([0-9]|[12][0-9]|3[0-2])$/.exec(value);
  if (!match) throw new Error("TrustedSubnet must be an explicit IPv4 CIDR.");
  const address = ipv4Number(match[1]);
  const prefix = Number(match[2]);
  const size = 2 ** (32 - prefix);
  const networkNumber = Math.floor(address / size) * size;
  if (address !== networkNumber) throw new Error("TrustedSubnet must use its canonical network address.");
  return { cidr: value, prefix, networkNumber, broadcastNumber: networkNumber + size - 1 };
}

function isPrivateNumber(value) {
  return (value >= 167772160 && value <= 184549375) ||
    (value >= 2886729728 && value <= 2887778303) ||
    (value >= 3232235520 && value <= 3232301055);
}

function isTrustedAddress(address, subnet) {
  try {
    const normalized = normalizeAddress(address);
    if (!normalized) return false;
    const range = typeof subnet === "string" ? parseSubnet(subnet) : subnet;
    const value = ipv4Number(normalized);
    if (!range || value < range.networkNumber || value > range.broadcastNumber) return false;
    return range.prefix >= 31 || (value !== range.networkNumber && value !== range.broadcastNumber);
  } catch {
    return false;
  }
}

function validatePort(value) {
  if (!/^[1-9]\d{0,4}$/.test(String(value)) || Number(value) > 65535) throw new Error("Ports must be integers from 1 to 65535.");
  return Number(value);
}

function validateConfig(options, allowMissingBind = false) {
  const subnet = parseSubnet(options.trustedSubnet);
  if (!isPrivateNumber(subnet.networkNumber) || !isPrivateNumber(subnet.broadcastNumber)) {
    throw new Error("The trusted subnet must be entirely inside a private RFC1918 LAN.");
  }
  const bindAddress = options.bindAddress || null;
  if (!bindAddress && !allowMissingBind) throw new Error("A specific private LAN bind address is required.");
  if (bindAddress && (!isPrivateNumber(ipv4Number(bindAddress)) || !isTrustedAddress(bindAddress, subnet))) {
    throw new Error("The bind address must be a usable private IPv4 address inside TrustedSubnet.");
  }
  return {
    bindAddress,
    trustedSubnet: subnet.cidr,
    port: validatePort(options.port === undefined ? 5100 : options.port),
    upstreamPort: validatePort(options.upstreamPort === undefined ? 5100 : options.upstreamPort),
    subnet
  };
}

function parseArguments(args) {
  const options = {};
  const names = new Map([["--bind", "bindAddress"], ["--trusted-subnet", "trustedSubnet"], ["--port", "port"], ["--upstream-port", "upstreamPort"]]);
  let mode = null;
  for (let i = 0; i < args.length; i++) {
    const arg = args[i];
    if (arg === "--manual" || arg === "--check-config") {
      if (mode) throw new Error("Specify exactly one manual or validation mode.");
      mode = arg;
    } else {
      const name = names.get(arg);
      if (!name || options[name] !== undefined || i + 1 >= args.length) throw new Error("Unknown, duplicate, or incomplete proxy option.");
      options[name] = args[++i];
    }
  }
  if (!mode) throw new Error("Use the manual PowerShell launcher with an explicitly trusted subnet.");
  return { config: validateConfig(options, mode === "--check-config"), checkOnly: mode === "--check-config" };
}

function createConnectionHandler(config, connectUpstream = () => net.createConnection({ host: "127.0.0.1", port: config.upstreamPort })) {
  return (client) => {
    if (!isTrustedAddress(client.remoteAddress, config.subnet)) {
      client.destroy();
      return;
    }
    let upstream;
    try { upstream = connectUpstream(); } catch { client.destroy(); return; }
    client.setTimeout(300000, () => client.destroy());
    upstream.setTimeout(300000, () => upstream.destroy());
    client.on("error", () => upstream.destroy());
    upstream.on("error", () => client.destroy());
    client.on("close", () => upstream.destroy());
    upstream.on("close", () => client.destroy());
    client.pipe(upstream);
    upstream.pipe(client);
  };
}

function main(args = process.argv.slice(2)) {
  let parsed;
  try { parsed = parseArguments(args); } catch (error) {
    console.error(error.message);
    process.exitCode = 2;
    return;
  }
  if (parsed.checkOnly) {
    console.log(JSON.stringify(parsed.config));
    return;
  }
  const config = parsed.config;
  const server = net.createServer(createConnectionHandler(config));
  server.maxConnections = 64;
  server.on("error", (error) => {
    console.error("LAN API proxy failed: " + (error.code || "unknown"));
    process.exit(1);
  });
  server.listen({ host: config.bindAddress, port: config.port, exclusive: true }, () => {
    console.log("Manual LAN API proxy ready on " + config.bindAddress + ":" + config.port + " for " + config.trustedSubnet);
  });
}

module.exports = { ipv4Number, normalizeAddress, parseSubnet, isTrustedAddress, validateConfig, parseArguments, createConnectionHandler };
if (require.main === module) main();
