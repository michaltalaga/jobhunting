import { useEffect, useState, type FormEvent, type MouseEvent } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router';
import { api, type JobSummary, type SetupStatus } from './api';
import { useJobs } from './jobs';
import { QueueStrip, useQueuePosition } from './Queue';
import {
  ApplicationBadge,
  ClosingTag,
  Header,
  MatchPill,
  NoReplyTag,
  ProcessingBadge,
  formatDate,
  formatDateTime,
  isAwaitingReply,
  isWorking,
  jobTitle,
} from './ui';

const dropped = (j: JobSummary) => j.applicationStatus === 'dropped';

// Every view except "Dropped" leaves dropped jobs out.
const filters: { key: string; label: string; match: (j: JobSummary) => boolean }[] = [
  { key: 'all', label: 'Active', match: () => true },
  { key: 'new', label: 'New', match: (j) => j.status === 'new' },
  { key: 'progress', label: 'In progress', match: (j) => !['new', 'processed', 'failed', 'onHold', 'readyToTailor'].includes(j.status) },
  { key: 'totailor', label: 'To tailor', match: (j) => j.status === 'readyToTailor' },
  { key: 'hold', label: 'On hold', match: (j) => j.status === 'onHold' },
  { key: 'ready', label: 'Ready to apply', match: (j) => j.status === 'processed' && j.applicationStatus === 'notApplied' },
  { key: 'applied', label: 'Applied', match: (j) => j.applicationStatus === 'applied' || j.applicationStatus === 'interview' },
  { key: 'noreply', label: 'No reply', match: isAwaitingReply },
  { key: 'offer', label: 'Offers', match: (j) => j.applicationStatus === 'offer' },
  { key: 'rejected', label: 'Rejected', match: (j) => j.applicationStatus === 'rejected' },
  { key: 'failed', label: 'Failed', match: (j) => j.status === 'failed' },
  { key: 'dropped', label: 'Dropped', match: dropped },
];

const inView = (key: string, match: (j: JobSummary) => boolean) => (j: JobSummary) =>
  key === 'dropped' ? match(j) : !dropped(j) && match(j);

/** Quick actions for one row, each shown only when it can run right now. */
function RowActions({ job }: { job: JobSummary }) {
  const { upsert } = useJobs();
  const [error, setError] = useState<string | null>(null);
  const busy = isWorking(job.status);
  const act = (e: MouseEvent, action: () => Promise<JobSummary | undefined>) => {
    e.stopPropagation();
    setError(null);
    action().then(upsert, (err: Error) => setError(err.message));
  };

  if (dropped(job))
    return (
      <button className="secondary small" title="Back to Not applied" onClick={(e) => act(e, () => api.setApplicationStatus(job.id, 'notApplied'))}>
        Restore
      </button>
    );

  // Held, new, failed or finished jobs can all be started; only running or already-queued ones can't.
  const idle = !busy && job.status !== 'queued';
  const canScore = idle && job.matchScore == null;
  const canTailor = idle && !job.hasResume;
  const pdfPending = job.render === 'queued' || job.render === 'rendering';
  const canRender = job.hasResume && !busy && !pdfPending && (!job.hasPdf || job.render === 'failed');
  return (
    <span className="row-actions" title={error ?? undefined}>
      {pdfPending && <span className="muted small">PDF…</span>}
      {canRender && (
        <button
          className="small"
          title={job.render === 'failed' ? `Last render failed: ${job.renderError ?? ''}` : 'Render the PDF'}
          onClick={(e) => act(e, () => api.render(job.id))}
        >
          Render
        </button>
      )}
      {canScore && (
        <button className="small" title="Extract the advert and score your match" onClick={(e) => act(e, () => api.score(job.id))}>
          Score
        </button>
      )}
      {canTailor && (
        <button className="small" title="Write the tailored resume (scores first if needed)" onClick={(e) => act(e, () => api.tailor(job.id))}>
          Tailor
        </button>
      )}
      {!busy && (
        <button className="secondary small" title="Not pursuing it: hide it and never process it" onClick={(e) => act(e, () => api.setApplicationStatus(job.id, 'dropped'))}>
          Drop
        </button>
      )}
      {error && <span className="error-text small">!</span>}
    </span>
  );
}

export function JobList() {
  const { jobs, loaded, error } = useJobs();
  const navigate = useNavigate();
  const [params, setParams] = useSearchParams();
  const active = filters.find((f) => f.key === params.get('filter')) ?? filters[0];
  const positionOf = useQueuePosition();
  const byMatch = params.get('sort') === 'match';

  const all = [...jobs.values()].sort((a, b) =>
    byMatch ? (b.matchScore ?? -1) - (a.matchScore ?? -1) || b.capturedAt.localeCompare(a.capturedAt) : b.capturedAt.localeCompare(a.capturedAt),
  );
  const visible = all.filter(inView(active.key, active.match));

  function setSort(match: boolean) {
    const next = new URLSearchParams(params);
    if (match) next.set('sort', 'match');
    else next.delete('sort');
    setParams(next);
  }

  return (
    <>
      <Header />
      <main className="page">
        <SetupBanner />
        <QueueStrip />
        <PastePanel />

        <nav className="filters">
          {filters.map((f) => (
            <button
              key={f.key}
              className={f === active ? 'chip active' : 'chip'}
              onClick={() => {
                const next = new URLSearchParams(params);
                if (f.key === 'all') next.delete('filter');
                else next.set('filter', f.key);
                setParams(next);
              }}
            >
              {f.label} <span className="count">{all.filter(inView(f.key, f.match)).length}</span>
            </button>
          ))}
        </nav>

        {error && <p className="alert error">Can't reach the server: {error}</p>}

        {loaded && all.length === 0 ? (
          <p className="empty">
            No jobs yet. Click the <strong>Job Hunting Capture</strong> button in Chrome while viewing a job advert, or paste one above.
          </p>
        ) : (
          <table className="jobs">
            <thead>
              <tr>
                <th>
                  <button className={byMatch ? 'th-sort' : 'th-sort active'} onClick={() => setSort(false)}>
                    Captured
                  </button>
                </th>
                <th>
                  <button className={byMatch ? 'th-sort active' : 'th-sort'} onClick={() => setSort(true)} title="Sort by match score">
                    Match
                  </button>
                </th>
                <th>Role</th>
                <th>Company</th>
                <th>Location</th>
                <th>Processing</th>
                <th>Application</th>
                <th>Actions</th>
              </tr>
            </thead>
            <tbody>
              {visible.map((job) => (
                <tr key={job.id} onClick={() => navigate(`/jobs/${job.id}`)}>
                  <td className="nowrap muted">{formatDateTime(job.capturedAt)}</td>
                  <td>
                    <MatchPill score={job.matchScore} />
                  </td>
                  <td>
                    <Link to={`/jobs/${job.id}`} onClick={(e) => e.stopPropagation()}>
                      {jobTitle(job)}
                    </Link>
                    {job.duplicateOf.length > 0 && (
                      <span className="tag warn" title="The same URL was captured more than once">
                        duplicate
                      </span>
                    )}
                    {job.pendingChanges > 0 && <span className="tag">changes queued</span>}
                    <ClosingTag job={job} />
                  </td>
                  <td>{job.company ?? '—'}</td>
                  <td className="muted">{job.location ?? '—'}</td>
                  <td className="nowrap">
                    <ProcessingBadge status={job.status} />
                    {positionOf(job.id) && <span className="muted small"> #{positionOf(job.id)}</span>}
                  </td>
                  <td className="nowrap">
                    <ApplicationBadge status={job.applicationStatus} />
                    {job.applicationStatus !== 'notApplied' && job.applicationStatusAt && (
                      <span className="muted small"> {formatDate(job.applicationStatusAt)}</span>
                    )}
                    <NoReplyTag job={job} />
                  </td>
                  <td className="nowrap">
                    <RowActions job={job} />
                  </td>
                </tr>
              ))}
              {loaded && visible.length === 0 && (
                <tr>
                  <td colSpan={8} className="empty">
                    Nothing here.
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        )}
      </main>
    </>
  );
}

/** First-run help: shown only while something stops jobs from being processed. */
function SetupBanner() {
  const [setup, setSetup] = useState<SetupStatus | null>(null);
  useEffect(() => {
    api.setup().then(setSetup, () => {});
  }, []);
  if (!setup || setup.problems.length === 0) return null;
  return (
    <div className="alert warn setup">
      <strong>Finish setup before capturing jobs:</strong>
      <ul>
        {setup.problems.map((p, i) => (
          <li key={i}>{p}</li>
        ))}
      </ul>
    </div>
  );
}

/** For adverts that aren't on a web page you can capture: paste the text instead. */
function PastePanel() {
  const navigate = useNavigate();
  const [open, setOpen] = useState(false);
  const [text, setText] = useState('');
  const [url, setUrl] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [existing, setExisting] = useState<string | null>(null);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    setExisting(null);
    try {
      const { id, created } = await api.paste(text, url);
      if (created) navigate(`/jobs/${id}`);
      else {
        setExisting(id);
        setBusy(false);
      }
    } catch (err) {
      setError((err as Error).message);
      setBusy(false);
    }
  }

  if (!open)
    return (
      <div className="paste-toggle">
        <button className="secondary" onClick={() => setOpen(true)}>
          + Paste a job advert
        </button>
      </div>
    );

  return (
    <form className="card paste" onSubmit={submit}>
      <h2>Paste a job advert</h2>
      <input type="url" placeholder="Advert URL (optional)" value={url} onChange={(e) => setUrl(e.target.value)} />
      <textarea
        placeholder="Paste the full job advert text here…"
        rows={10}
        value={text}
        onChange={(e) => setText(e.target.value)}
        autoFocus
      />
      {error && <p className="alert error">{error}</p>}
      {existing && (
        <p className="alert warn">
          This advert is already in your list, so nothing was added. <Link to={`/jobs/${existing}`}>Open it</Link>
        </p>
      )}
      <div className="row">
        <button type="submit" disabled={busy || !text.trim()}>
          {busy ? 'Adding…' : 'Add to queue'}
        </button>
        <button type="button" className="secondary" onClick={() => setOpen(false)}>
          Cancel
        </button>
      </div>
    </form>
  );
}
