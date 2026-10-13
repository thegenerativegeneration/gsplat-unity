// Writes Fixtures~/lod-golden.json by running PlayCanvas engine v2.23.2's LOD table and budget balancer.
// Setup: git clone --depth 1 --branch v2.23.2 --filter=blob:none --sparse https://github.com/playcanvas/engine.git engine
//        (cd engine && git sparse-checkout set src/scene/gsplat-unified)
import { readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { GSplatLodTable } from './engine/src/scene/gsplat-unified/gsplat-lod-table.js';
import { GSplatBudgetBalancer } from './engine/src/scene/gsplat-unified/gsplat-budget-balancer.js';

const here = dirname(fileURLToPath(import.meta.url));
const fixtures = join(here, '..', 'Fixtures~');

function rng(seed) {
    return () => {
        seed |= 0; seed = (seed + 0x6D2B79F5) | 0;
        let t = Math.imul(seed ^ (seed >>> 15), 1 | seed);
        t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t;
        return ((t ^ (t >>> 14)) >>> 0) / 4294967296;
    };
}

// GSplatOctree._computeBoundsExcess, verbatim
function boundsExcess(boundsFlat, nodeCount) {
    const excess = new Float32Array(nodeCount * 3);
    if (nodeCount === 0) return excess;
    const half = new Float32Array(nodeCount);
    for (let axis = 0; axis < 3; axis++) {
        for (let i = 0; i < nodeCount; i++) half[i] = (boundsFlat[i * 6 + 3 + axis] - boundsFlat[i * 6 + axis]) * 0.5;
        const sorted = half.slice().sort();
        const typical = sorted[nodeCount >> 1];
        for (let i = 0; i < nodeCount; i++) excess[i * 3 + axis] = Math.max(0, half[i] - typical);
    }
    return excess;
}

// GSplatOctreeInstance.evaluateNodeDistances inner loop, verbatim apart from inputs
function distances(boundsFlat, excessFlat, shrink, cam, fwd, distanceScale, lodBehindPenalty) {
    const n = boundsFlat.length / 6;
    const out = new Array(n);
    const distanceScaleSq = distanceScale * distanceScale;
    const penalty = lodBehindPenalty > 1 ? lodBehindPenalty - 1 : 0;
    const [px, py, pz] = cam;
    const [fwx, fwy, fwz] = fwd;
    for (let nodeIndex = 0; nodeIndex < n; nodeIndex++) {
        const b = nodeIndex * 6;
        let minX = boundsFlat[b], maxX = boundsFlat[b + 3];
        let minY = boundsFlat[b + 1], maxY = boundsFlat[b + 4];
        let minZ = boundsFlat[b + 2], maxZ = boundsFlat[b + 5];
        if (shrink > 0) {
            const e = nodeIndex * 3;
            const sx = excessFlat[e] * shrink, sy = excessFlat[e + 1] * shrink, sz = excessFlat[e + 2] * shrink;
            minX += sx; maxX -= sx; minY += sy; maxY -= sy; minZ += sz; maxZ -= sz;
        }
        let qx = px; if (qx < minX) qx = minX; else if (qx > maxX) qx = maxX;
        let qy = py; if (qy < minY) qy = minY; else if (qy > maxY) qy = maxY;
        let qz = pz; if (qz < minZ) qz = minZ; else if (qz > maxZ) qz = maxZ;
        const dx = qx - px, dy = qy - py, dz = qz - pz;
        const distanceSq = dx * dx + dy * dy + dz * dz;
        let penaltyFactor = 1;
        if (penalty > 0 && distanceSq > 0.0001) {
            const dot = fwx * dx + fwy * dy + fwz * dz;
            if (dot < 0) penaltyFactor = 1 + (-dot / Math.sqrt(distanceSq)) * penalty;
        }
        out[nodeIndex] = distanceSq * penaltyFactor * penaltyFactor * distanceScaleSq;
    }
    return out;
}

function fromLodMeta(path) {
    const meta = JSON.parse(readFileSync(path, 'utf8'));
    const leaves = [];
    const walk = (node) => node.children ? node.children.forEach(walk) : leaves.push(node);
    walk(meta.tree);
    const levels = meta.lodLevels;
    const bounds = [], counts = [];
    for (const leaf of leaves) {
        bounds.push(...leaf.bound.min, ...leaf.bound.max);
        for (let l = 0; l < levels; l++) counts.push(leaf.lods[l] ? leaf.lods[l].count : 0);
    }
    return { levels, bounds, counts };
}

function synthetic(seed, nodes, levels, gapRate) {
    const r = rng(seed);
    const bounds = [], counts = [];
    for (let i = 0; i < nodes; i++) {
        const cx = (r() - 0.5) * 400, cy = r() * 30, cz = (r() - 0.5) * 400, h = 1 + r() * (r() < 0.1 ? 40 : 8);
        bounds.push(cx - h, cy - h, cz - h, cx + h, cy + h, cz + h);
        let c = Math.floor(2000 + r() * 60000);
        const coarseOnly = r() < 0.05;
        for (let l = 0; l < levels; l++) {
            const gap = (l > 0 && r() < gapRate) || (coarseOnly && l < levels - 1);
            counts.push(gap ? 0 : c);
            c = Math.max(1, Math.floor(c / 3));
        }
    }
    return { levels, bounds, counts };
}

function runCase(name, instances, camera, budget, limit, params) {
    const octreeInstances = new Map();
    const outInstances = [];
    for (const inst of instances) {
        const nodeCount = inst.bounds.length / 6;
        const nodes = [];
        for (let n = 0; n < nodeCount; n++) {
            const lods = [];
            for (let l = 0; l < inst.levels; l++) lods.push({ count: inst.counts[n * inst.levels + l] });
            nodes.push({ lods });
        }
        const octree = { nodes };
        const lodTable = new GSplatLodTable(octree, 0, inst.levels - 1);
        const boundsFlat = new Float32Array(inst.bounds);
        const excess = boundsExcess(boundsFlat, nodeCount);
        const d = distances(boundsFlat, excess, params.shrink, camera.pos, camera.fwd, camera.distanceScale, params.behindPenalty);
        const nodeInfos = d.map(v => ({ worldDistanceSq: v, optimalLod: -2 }));
        const placement = { lodBaseDistance: inst.baseDistance, lodMultiplier: inst.multiplier };
        octreeInstances.set(placement, { octree, lodTable, nodeInfos, placement, rangeMin: 0 });
        outInstances.push({
            levels: inst.levels, bounds: Array.from(boundsFlat), counts: inst.counts,
            baseDistance: inst.baseDistance, multiplier: inst.multiplier,
            excess: Array.from(excess), distanceSq: d, nodeInfos
        });
    }
    new GSplatBudgetBalancer().balance(octreeInstances, budget, limit);
    return {
        name, budget, limit, shrink: params.shrink, behindPenalty: params.behindPenalty,
        camera: { pos: camera.pos, fwd: camera.fwd, distanceScale: camera.distanceScale },
        instances: outInstances.map(o => ({
            levels: o.levels, bounds: o.bounds, counts: o.counts, baseDistance: o.baseDistance, multiplier: o.multiplier,
            excess: o.excess, distanceSq: o.distanceSq, optimalLod: o.nodeInfos.map(n => n.optimalLod)
        }))
    };
}

const params = { shrink: 0.75, behindPenalty: 1.5 };
const fx = fromLodMeta(join(fixtures, 'lod', 'lod-meta.json'));
const big = synthetic(7, 400, 5, 0.2);
const other = synthetic(11, 150, 4, 0.0);
const cams = [
    { pos: [0, 2, 0], fwd: [0, 0, -1], distanceScale: 1 },
    { pos: [30, 5, -40], fwd: [0.5, 0, 0.75], distanceScale: 1.3 },
    { pos: [6, 4, 6], fwd: [0, 0, 1], distanceScale: 0.9 } // inside a fixture leaf
];
const inst = (d, b = 5, m = 3) => ({ ...d, baseDistance: b, multiplier: m });
const cases = [];
cams.forEach((c, i) => {
    cases.push(runCase(`fixture-cam${i}-tight`, [inst(fx)], c, 2500, false, params));
    cases.push(runCase(`fixture-cam${i}-all`, [inst(fx)], c, 1e9, false, params));
    cases.push(runCase(`fixture-cam${i}-none`, [inst(fx)], c, 10, false, params));
    cases.push(runCase(`gaps-cam${i}`, [inst(big)], c, 2e6, false, params));
    cases.push(runCase(`multi-cam${i}`, [inst(big), inst(other, 8, 2.5)], c, 1.5e6, false, params));
    cases.push(runCase(`multi-cam${i}-limit`, [inst(big), inst(other, 8, 2.5)], c, 1.5e6, true, params));
});
writeFileSync(join(fixtures, 'lod-golden.json'), JSON.stringify({ engine: 'v2.23.2', cases }));
console.log(`${cases.length} cases written`);
