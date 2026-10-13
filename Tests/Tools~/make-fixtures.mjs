// Generates the committed test fixtures. Run with Node 22: `npm ci && node make-fixtures.mjs`.
import { execFileSync } from 'node:child_process';
import { mkdirSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const out = join(here, '..', 'Fixtures~');
const cli = join(here, 'node_modules', '.bin', 'splat-transform');
const SH_REST = 45;

// mulberry32: deterministic, so the fixtures are reproducible byte for byte
function rng(seed) {
    return () => {
        seed |= 0; seed = (seed + 0x6D2B79F5) | 0;
        let t = Math.imul(seed ^ (seed >>> 15), 1 | seed);
        t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
        return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
    };
}

function writePly(path, count, seed) {
    const r = rng(seed);
    const props = ['x', 'y', 'z', 'f_dc_0', 'f_dc_1', 'f_dc_2'];
    for (let i = 0; i < SH_REST; i++) props.push(`f_rest_${i}`);
    props.push('opacity', 'scale_0', 'scale_1', 'scale_2', 'rot_0', 'rot_1', 'rot_2', 'rot_3');
    const header = `ply\nformat binary_little_endian 1.0\nelement vertex ${count}\n` +
        props.map(p => `property float ${p}`).join('\n') + '\nend_header\n';
    const data = new Float32Array(count * props.length);
    for (let i = 0; i < count; i++) {
        const o = i * props.length;
        // four clusters in a 24 x 8 x 24 m box so the LOD writer makes several leaves
        const c = i % 4;
        data[o] = (c % 2 ? 6 : -6) + (r() - 0.5) * 10;
        data[o + 1] = r() * 8;
        data[o + 2] = (c < 2 ? 6 : -6) + (r() - 0.5) * 10;
        for (let k = 3; k < 6; k++) data[o + k] = (r() - 0.5) * 3;
        for (let k = 0; k < SH_REST; k++) data[o + 6 + k] = (r() - 0.5) * 0.6;
        const b = 6 + SH_REST;
        data[o + b] = (r() - 0.3) * 8;
        for (let k = 1; k <= 3; k++) data[o + b + k] = Math.log(0.02 + r() * 0.2);
        let q = [r() - 0.5, r() - 0.5, r() - 0.5, r() - 0.5];
        const n = Math.hypot(...q);
        q = q.map(v => v / n);
        for (let k = 0; k < 4; k++) data[o + b + 4 + k] = q[k];
    }
    writeFileSync(path, Buffer.concat([Buffer.from(header, 'ascii'), Buffer.from(data.buffer)]));
}

const st = (...args) => execFileSync(cli, ['-w', ...args], { stdio: 'inherit', cwd: out });

rmSync(out, { recursive: true, force: true });
mkdirSync(out, { recursive: true });
writePly(join(out, 'synth.ply'), 6000, 20261013);
st('synth.ply', 'synth.sog');
mkdirSync(join(out, 'synth-folder'));
st('synth.ply', 'synth-folder/meta.json');
st('synth-folder/meta.json', 'synth-roundtrip.ply');
st('synth.ply', '-d', '50%', 'lvl1.ply');
st('synth.ply', '-d', '20%', 'lvl2.ply');
mkdirSync(join(out, 'lod'));
st('synth.ply', '-l', '0', 'lvl1.ply', '-l', '1', 'lvl2.ply', '-l', '2',
   '--lod-chunk-count', '1', '--lod-chunk-extent', '4', '--lod-chunk-min', '1', 'lod/lod-meta.json');
st('lod/0_0/meta.json', 'lod-0_0-roundtrip.ply');
rmSync(join(out, 'lvl1.ply')); rmSync(join(out, 'lvl2.ply'));
console.log('fixtures written to', out);
