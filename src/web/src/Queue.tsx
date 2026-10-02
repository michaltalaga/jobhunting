import { useEffect, useState } from 'react';
import { Link } from 'react-router';
import { api, type JobSummary } from './api';
import { useJobs } from './jobs';
import { ProcessingBadge, isWorking, jobTitle } from './ui';

const label = (job: JobSummary) => [jobTitle(job), job.company].filter(Boolean).join(' · ');

/** Ticks every second: how long the job has been in its current step. */
function Elapsed({ since }: { since: string }) {
  const [now, setNow] = useState(Date.now());
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, []);
  const seconds = Math.max(0, Math.floor((now - Date.parse(since)) / 1000));
  return (
    <span className="muted small">
      {Math.floor(seconds / 60)}m {String(seconds % 60).padStart(2, '0')}s
    </span>
  );
}

function useAction() {
  const [error, setError] = useState<string | null>(null);
  const run = (action: Promise<unknown>) => {
    setError(null);
    action.catch((e: Error) => setError(e.message));
  };
  return { error, run };
}

/** The list page's view of the worker: what runs now, what waits (in order), and the controls. */
export function QueueStrip() {
  const { jobs, queue, setQueue, upsert } = useJobs();
  const { error, run } = useAction();
  const running = queue.running.map((id) => jobs.get(id)).filter((j): j is JobSummary => !!j);
  const onHold = [...jobs.values()].filter((j) => j.status === 'onHold').length;
  if (running.length === 0 && queue.waiting.length === 0 && !queue.paused && onHold === 0) return null;

  return (
    <section className="card queue">
      <div className="queue-head">
        <h2>Queue</h2>
        <span className="muted small">
          {queue.paused
            ? 'Paused: running jobs finish, nothing new starts.'
            : `${running.length} running · ${queue.waiting.length} waiting`}
          {onHold > 0 && (
            <>
              {' · '}
              <Link to="/?filter=hold">{onHold} on hold</Link>
            </>
          )}
        </span>
        <span className="spacer" />
        {queue.paused ? (
          <button onClick={() => run(api.resumeQueue().then(setQueue))}>Resume queue</button>
        ) : (
          <button className="secondary" onClick={() => run(api.pauseQueue().then(setQueue))}>
            Pause queue
          </button>
        )}
      </div>
      {(running.length > 0 || queue.waiting.length > 0) && (
        <ol className="queue-list">
          {running.map((job) => (
            <li key={job.id} className="running">
              <ProcessingBadge status={job.status} />
              <Link to={`/jobs/${job.id}`}>{label(job)}</Link>
              {isWorking(job.status) && <Elapsed since={job.updatedAt} />}
              <span className="spacer" />
              <button className="danger small" title="Stop the Claude call and put the job on hold" onClick={() => run(api.hold(job.id))}>
                Stop
              </button>
            </li>
          ))}
          {queue.waiting.map((id, i) => {
            const job = jobs.get(id);
            return (
              <li key={id}>
                <span className="position">#{i + 1}</span>
                <Link to={`/jobs/${id}`}>{job ? label(job) : id}</Link>
                <span className="spacer" />
                {i > 0 && (
                  <button className="secondary small" onClick={() => run(api.prioritize(id).then(setQueue))}>
                    Run next
                  </button>
                )}
                <button className="secondary small" title="Take it out of the queue; Resume puts it back" onClick={() => run(api.hold(id).then(upsert))}>
                  Hold
                </button>
              </li>
            );
          })}
        </ol>
      )}
      {error && <p className="alert error">{error}</p>}
    </section>
  );
}

/** The job page's queue controls: position, Run next / Hold while waiting, Stop while running, Resume when held. */
export function QueueControls({ job }: { job: JobSummary }) {
  const { queue, setQueue, upsert } = useJobs();
  const { error, run } = useAction();
  const position = queue.waiting.indexOf(job.id);
  const running = queue.running.includes(job.id);

  return (
    <>
      {position >= 0 && (
        <p className="muted small">
          #{position + 1} in the queue{queue.paused && ' (the queue is paused)'}.
        </p>
      )}
      <div className="row">
        {position > 0 && (
          <button className="secondary" onClick={() => run(api.prioritize(job.id).then(setQueue))}>
            Run next
          </button>
        )}
        {position >= 0 && (
          <button className="secondary" onClick={() => run(api.hold(job.id).then(upsert))}>
            Hold
          </button>
        )}
        {running && (
          <button className="danger" onClick={() => run(api.hold(job.id))}>
            Stop
          </button>
        )}
        {job.status === 'onHold' && <button onClick={() => run(api.resume(job.id).then(upsert))}>Resume</button>}
      </div>
      {job.status === 'onHold' && (
        <p className="muted small">On hold: it keeps everything done so far. Change requests and settings apply when you resume.</p>
      )}
      {error && <p className="alert error">{error}</p>}
    </>
  );
}

/** "#3" for a waiting job, for the job list's Processing column. */
export function useQueuePosition() {
  const { queue } = useJobs();
  return (id: string) => {
    const i = queue.waiting.indexOf(id);
    return i >= 0 ? i + 1 : null;
  };
}
