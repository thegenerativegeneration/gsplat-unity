// Copyright (c) 2026 TheGenerativeGeneration
// SPDX-License-Identifier: MIT

using System.Collections.Generic;

namespace Gsplat
{
    internal readonly struct UploadStep
    {
        public readonly object Tag;
        public readonly int Source;
        public readonly int Destination;
        public readonly int Count;
        public readonly bool Completes;

        public UploadStep(object tag, int source, int destination, int count, bool completes)
        {
            Tag = tag;
            Source = source;
            Destination = destination;
            Count = count;
            Completes = completes;
        }
    }

    // Spreads decoded chunks over frames so one frame never uploads more than the budget.
    internal sealed class UploadScheduler
    {
        sealed class Job
        {
            public object Tag;
            public int Count;
            public int Destination;
            public int Done;
        }

        readonly LinkedList<Job> m_jobs = new();

        public bool IsEmpty => m_jobs.Count == 0;

        public void Enqueue(object tag, int count, int destination) =>
            m_jobs.AddLast(new Job { Tag = tag, Count = count, Destination = destination });

        public void Cancel(object tag)
        {
            for (var n = m_jobs.First; n != null;)
            {
                var next = n.Next;
                if (Equals(n.Value.Tag, tag)) m_jobs.Remove(n);
                n = next;
            }
        }

        public int Step(int budget, List<UploadStep> output)
        {
            int scheduled = 0;
            while (m_jobs.First != null && scheduled < budget)
            {
                var job = m_jobs.First.Value;
                int n = System.Math.Min(job.Count - job.Done, budget - scheduled);
                bool completes = job.Done + n == job.Count;
                output.Add(new UploadStep(job.Tag, job.Done, job.Destination + job.Done, n, completes));
                job.Done += n;
                scheduled += n;
                if (completes) m_jobs.RemoveFirst();
            }
            return scheduled;
        }
    }
}
