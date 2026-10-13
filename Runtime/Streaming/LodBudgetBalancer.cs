// Copyright (c) 2026 TheGenerativeGeneration
// SPDX-License-Identifier: MIT
// Port of https://github.com/playcanvas/engine/blob/v2.23.2/src/scene/gsplat-unified/gsplat-budget-balancer.js
//
// A leaf at world distance d draws band floor(1 + log_m(d / (s * b))); one scene-wide scale s is fitted to the budget
// with a histogram over ln s (switch points are closed-form), refined inside the cut bin with sub-bins. No sort, no queue.

using System;
using System.Collections.Generic;

namespace Gsplat
{
    internal sealed class LodInstance
    {
        public LodTable Table;
        public double[] DistanceSq;
        public float BaseDistance = 5f;
        public float Multiplier = 3f;
        public int[] OptimalLod;
    }

    internal sealed class LodBudgetBalancer
    {
        const int k_numScaleBins = 1024;
        const int k_numSubBins = 1024;
        const double k_logScaleMin = -48;
        const double k_logScaleMax = 48;
        const double k_binScale = k_numScaleBins / (k_logScaleMax - k_logScaleMin);
        const int k_lastBin = k_numScaleBins - 1;
        const int k_lastSubBin = k_numSubBins - 1;
        const int k_unitScaleBin = k_numScaleBins / 2;
        const double k_minDistanceSq = 1e-12;
        const double k_halfBinScale = k_binScale * 0.5;

        readonly double[] m_histogram = new double[Math.Max(k_numScaleBins, k_numSubBins)];
        double[] m_position = new double[0];

        static int Clamp(double t, int last) => t < 0 ? 0 : (t >= last + 1 ? last : (int)t);

        public void Balance(IReadOnlyList<LodInstance> instances, double budget, bool limit)
        {
            int nodeTotal = 0;
            double total = 0, finestTotal = 0;
            foreach (var inst in instances)
            {
                nodeTotal += inst.DistanceSq.Length;
                total += inst.Table.TotalCoarsestCount;
                finestTotal += inst.Table.TotalFinestCount;
            }
            if (nodeTotal == 0) return;

            if (total >= budget)
            {
                AssignChainEnd(instances, false);
                return;
            }
            if (!limit && finestTotal <= budget)
            {
                AssignChainEnd(instances, true);
                return;
            }

            if (m_position.Length < nodeTotal)
                m_position = new double[Math.Max(nodeTotal, Math.Max(m_position.Length * 2, 1024))];
            var position = m_position;

            double unitTotal = 0;
            int baseIndex = 0;
            foreach (var inst in instances)
            {
                var t = inst.Table;
                double step = Math.Log(inst.Multiplier) * k_binScale;
                double offset = (-Math.Log(inst.BaseDistance) - k_logScaleMin) * k_binScale - (t.RangeMin - 1) * step;
                for (int n = 0; n < inst.DistanceSq.Length; n++)
                {
                    int row = n * t.Span;
                    if (t.BandLod[row] < 0) continue;
                    double dSq = inst.DistanceSq[n];
                    double p = Math.Log(dSq > k_minDistanceSq ? dSq : k_minDistanceSq) * k_halfBinScale + offset;
                    position[baseIndex + n] = p;

                    if (limit)
                    {
                        int b = t.Span - 1;
                        while (b > 0)
                        {
                            if (Clamp(p - b * step, k_lastBin) >= k_unitScaleBin) break;
                            b--;
                        }
                        unitTotal += t.BandCount[row + b];
                    }
                }
                baseIndex += inst.DistanceSq.Length;
            }

            int cutBin = k_unitScaleBin;
            int cutSub = 0;
            if (!limit || unitTotal > budget)
            {
                int stopBin = limit ? k_unitScaleBin : k_numScaleBins;
                Array.Clear(m_histogram, 0, k_numScaleBins);
                Accumulate(instances, -1);

                cutBin = 0;
                while (cutBin < stopBin)
                {
                    double next = total + m_histogram[cutBin];
                    if (next > budget) break;
                    total = next;
                    cutBin++;
                }

                if (cutBin < stopBin)
                {
                    Array.Clear(m_histogram, 0, k_numSubBins);
                    Accumulate(instances, cutBin);
                    while (cutSub < k_numSubBins)
                    {
                        double next = total + m_histogram[cutSub];
                        if (next > budget) break;
                        total = next;
                        cutSub++;
                    }
                }
            }

            baseIndex = 0;
            foreach (var inst in instances)
            {
                var t = inst.Table;
                double step = Math.Log(inst.Multiplier) * k_binScale;
                for (int n = 0; n < inst.DistanceSq.Length; n++)
                {
                    int row = n * t.Span;
                    if (t.BandLod[row] < 0)
                    {
                        inst.OptimalLod[n] = -1;
                        continue;
                    }

                    double p = position[baseIndex + n];
                    int b = t.Span - 1;
                    while (b > 0)
                    {
                        double tt = p - b * step;
                        int bin = Clamp(tt, k_lastBin);
                        if (bin > cutBin) break;
                        if (bin == cutBin)
                        {
                            double sub = (tt - cutBin) * k_numSubBins;
                            if (Clamp(sub, k_lastSubBin) >= cutSub) break;
                        }
                        b--;
                    }
                    inst.OptimalLod[n] = t.BandLod[row + b];
                }
                baseIndex += inst.DistanceSq.Length;
            }
        }

        void Accumulate(IReadOnlyList<LodInstance> instances, int bin)
        {
            var position = m_position;
            var histogram = m_histogram;
            bool scan = bin < 0 || bin == 0 || bin == k_lastBin;

            int baseIndex = 0;
            foreach (var inst in instances)
            {
                var t = inst.Table;
                int len = inst.DistanceSq.Length;
                double step = Math.Log(inst.Multiplier) * k_binScale;
                double invStep = 1 / step;
                for (int n = 0; n < len; n++)
                {
                    int row = n * t.Span;
                    if (t.BandLod[row] < 0) continue;
                    double p = position[baseIndex + n];

                    int b = scan ? 1 : (int)Math.Floor((p - bin) * invStep);
                    int end = scan ? t.Span : Math.Min(b + 2, t.Span);
                    if (b < 1) b = 1;

                    for (; b < end; b++)
                    {
                        int delta = t.BandCount[row + b - 1] - t.BandCount[row + b];
                        if (delta == 0) continue;
                        double tt = p - b * step;
                        int tb = Clamp(tt, k_lastBin);
                        if (bin < 0)
                        {
                            histogram[tb] += delta;
                        }
                        else if (tb == bin)
                        {
                            double sub = (tt - bin) * k_numSubBins;
                            histogram[Clamp(sub, k_lastSubBin)] += delta;
                        }
                    }
                }
                baseIndex += len;
            }
        }

        static void AssignChainEnd(IReadOnlyList<LodInstance> instances, bool finest)
        {
            foreach (var inst in instances)
            {
                var t = inst.Table;
                int offset = finest ? 0 : t.Span - 1;
                for (int n = 0; n < inst.DistanceSq.Length; n++)
                    inst.OptimalLod[n] = t.BandLod[n * t.Span + offset];
            }
        }
    }
}
