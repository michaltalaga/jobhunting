import { createContext, useCallback, useContext, useEffect, useState, type ReactNode } from 'react';
import { api, type JobSummary } from './api';

interface JobsContextValue {
  jobs: Map<string, JobSummary>;
  loaded: boolean;
  connected: boolean;
  error: string | null;
  upsert: (job: JobSummary) => void;
}

const JobsContext = createContext<JobsContextValue | null>(null);

/** Keeps every job summary in memory, live-updated from the server's event stream. */
export function JobsProvider({ children }: { children: ReactNode }) {
  const [jobs, setJobs] = useState<Map<string, JobSummary>>(new Map());
  const [loaded, setLoaded] = useState(false);
  const [connected, setConnected] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const upsert = useCallback((job: JobSummary) => {
    setJobs((prev) => {
      const existing = prev.get(job.id);
      if (existing && existing.updatedAt > job.updatedAt) return prev; // an older response arrived late
      return new Map(prev).set(job.id, job);
    });
  }, []);

  useEffect(() => {
    const reload = () =>
      api
        .list()
        .then((list) => {
          setJobs(new Map(list.map((j) => [j.id, j])));
          setLoaded(true);
          setError(null);
        })
        .catch((e: Error) => setError(e.message));

    reload();
    const source = new EventSource('/api/events');
    source.onopen = () => {
      setConnected(true);
      reload(); // catch up on anything missed while disconnected
    };
    source.onerror = () => setConnected(false);
    source.addEventListener('job', (e) => upsert(JSON.parse((e as MessageEvent).data) as JobSummary));
    source.addEventListener('deleted', (e) => {
      const { id } = JSON.parse((e as MessageEvent).data) as { id: string };
      setJobs((prev) => {
        const next = new Map(prev);
        next.delete(id);
        return next;
      });
    });
    return () => source.close();
  }, [upsert]);

  return <JobsContext.Provider value={{ jobs, loaded, connected, error, upsert }}>{children}</JobsContext.Provider>;
}

export function useJobs() {
  const value = useContext(JobsContext);
  if (!value) throw new Error('useJobs must be used inside JobsProvider');
  return value;
}
