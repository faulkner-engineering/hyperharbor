// Demo layer for the trailer, injected into the client before the page loads.
// It wraps Tauri's invoke so the recording shows made-up values and nothing is created on the host:
// - one host, renamed, with a made-up address; other paired hosts are hidden
// - only the "Faulkner Engineering LLC" VM is listed, with a made-up address
// - elevation always reads as active, so the passphrase prompt never appears
// - saving a machine profile and creating a VM from it happen in memory only
// - Connect shows its progress and toast, but no Remote Desktop session starts
// Catalogs, package search, ISOs, and the profile editor's data still come from the real host.
(() => {
  const HOST_NAME = 'HOME-PC';
  const KEEP_VM = 'Faulkner Engineering LLC';
  const state = { profiles: [], vms: [], jobs: {} };
  const iso = (offsetMs = 0) => new Date(Date.now() + offsetMs).toISOString();
  const uuid = () => crypto.randomUUID();
  let template = null; // the kept VM, cloned for VMs created in the demo

  const steps = [
    [0, 'Writing the answer file', 1],
    [700, 'Creating the virtual hard disk', 12],
    [2200, 'Creating the virtual hard disk', 30],
    [3000, 'Defining the virtual machine', 35],
    [3800, 'Adding processors, memory, disk, DVDs, and network', 50],
    [4800, 'Adding a virtual TPM', 80],
    [5600, 'Finishing', 95],
  ];
  const DONE_MS = 6400;

  function summary(stored) {
    const p = stored.profile;
    return {
      id: stored.id, name: p.name, description: p.description ?? null, os: p.os,
      processors: p.compute?.processors ?? null, memoryMb: p.compute?.memory?.startupMb ?? null,
      gpu: !!p.gpu?.enabled, tpm: p.firmware?.tpm ?? null, backup: !!p.backup?.enabled,
      softwareCount: (p.install?.length ?? 0), updatedAt: stored.updatedAt, error: null,
    };
  }

  function job(id) {
    const j = state.jobs[id];
    const elapsed = Date.now() - j.started;
    if (elapsed >= DONE_MS) {
      if (!j.vmAdded) {
        j.vmAdded = true;
        state.vms.push({
          ...(template ?? {}), id: j.vmId, name: j.name, state: 'running', uptimeSeconds: 0,
          cpuUsagePercent: 38, memoryAssignedMb: j.memoryMb, ipAddresses: [], rdpAvailable: false,
          provisioned: true, installState: 'installing', guestOs: { family: 'windows', name: null },
          createdAt: Date.now(),
        });
      }
      return { ...j.base, vmId: j.vmId, state: 'succeeded', step: 'Done', percentComplete: 100, updatedAt: iso() };
    }
    const [, step, percent] = steps.filter(([at]) => at <= elapsed).at(-1);
    return { ...j.base, step, percentComplete: percent, updatedAt: iso() };
  }

  function rewrite(cmd, args, result) {
    if (cmd === 'list_hosts') {
      const local = result.filter((h) => h.isLocal);
      return (local.length ? local : result.slice(0, 1)).map((h) => ({
        ...h, displayName: HOST_NAME, hostName: HOST_NAME, addresses: ['192.168.1.20'], tailscale: null, source: 'discovered',
      }));
    }
    if (cmd === 'list_vms') {
      const kept = result.filter((v) => v.name === KEEP_VM).map((v) => ({
        ...v, ipAddresses: v.ipAddresses.length ? ['192.168.1.42'] : [],
        remoteDesktop: v.remoteDesktop?.address ? { ...v.remoteDesktop, address: '192.168.1.42' } : v.remoteDesktop,
      }));
      if (kept[0]) template = kept[0];
      const created = state.vms.map(({ createdAt, ...v }) => ({ ...v, uptimeSeconds: Math.floor((Date.now() - createdAt) / 1000) }));
      return [...kept, ...created];
    }
    if (cmd === 'get_host_resource' && args?.resource === 'machineProfiles') {
      return [...result, ...state.profiles.map(summary)];
    }
    return result;
  }

  async function fake(cmd, args) {
    if (cmd === 'get_elevation') return { configured: true, active: true, expiresAt: iso(5 * 60_000) };
    // Connect: the button shows Connecting… for about as long as a real connect, then the toast; no Remote
    // Desktop window opens and no password is rotated. Null means no extra notice (such as camera sharing).
    if (cmd === 'connect_vm') {
      await new Promise((r) => setTimeout(r, 1600));
      return null;
    }
    if (cmd === 'save_profile' && args?.kind === 'machine') {
      const existing = state.profiles.find((p) => p.id === args.profileId);
      const stored = { id: existing?.id ?? uuid(), updatedAt: iso(), profile: args.profile };
      state.profiles = [...state.profiles.filter((p) => p.id !== stored.id), stored];
      await new Promise((r) => setTimeout(r, 450));
      return stored;
    }
    if (cmd === 'get_profile' && args?.kind === 'machine') {
      const stored = state.profiles.find((p) => p.id === args.profileId);
      if (stored) return stored;
    }
    if (cmd === 'create_vm_from_profile' && state.profiles.some((p) => p.id === args.profileId)) {
      const id = uuid();
      const memoryMb = args.request?.compute?.memory?.startupMb ?? 4096;
      const base = { id, kind: 'createVm', vmId: null, state: 'running', step: 'Writing the answer file', percentComplete: 1,
        createdAt: iso(), updatedAt: iso(), error: null };
      state.jobs[id] = { base, started: Date.now(), name: args.request.name, vmId: uuid(), memoryMb };
      return base;
    }
    if (cmd === 'get_job' && state.jobs[args?.jobId]) return job(args.jobId);
    return undefined;
  }

  // Tauri's invoke and ipc are read-only, but its IPC sends each command with the global fetch to
  // http://ipc.localhost/<command> (JSON body in, JSON body out, Tauri-Response: ok or error).
  const realFetch = window.fetch.bind(window);
  const json = (value) => new Response(JSON.stringify(value ?? null), {
    headers: { 'Content-Type': 'application/json', 'Tauri-Response': 'ok' },
  });
  window.fetch = async (input, init) => {
    const url = typeof input === 'string' ? input : input?.url ?? '';
    if (!url.startsWith('http://ipc.localhost/')) return realFetch(input, init);
    const cmd = decodeURIComponent(url.slice('http://ipc.localhost/'.length).split('?')[0]);
    let args = {};
    try { if (typeof init?.body === 'string') args = JSON.parse(init.body); } catch { /* binary payloads pass through */ }
    const faked = await fake(cmd, args);
    if (faked !== undefined) return json(faked);
    const response = await realFetch(input, init);
    if (response.headers.get('Tauri-Response') !== 'ok' || !(response.headers.get('content-type') ?? '').startsWith('application/json')) {
      return response;
    }
    return json(rewrite(cmd, args, await response.json()));
  };
  window.__demo = state;
})();
