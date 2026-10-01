import { useEffect, useState, type FormEvent } from 'react';
import { Link, useNavigate, useSearchParams } from 'react-router';
import { api, type JobSummary, type SetupStatus } from './api';
import { useJobs } from './jobs';
import { ApplicationBadge, ClosingTag, Header, ProcessingBadge, formatDate, formatDateTime, jobTitle } from './ui';

const filters: { key: string; label: string; match: (j: JobSummary) => boolean }[] = [
  { key: 'all', label: 'All', match: () => true },
  { key: 'progress', label: 'In progress', match: (j) => j.status !== 'processed' && j.status !== 'failed' },
  { key: 'ready', label: 'Ready to apply', match: (j) => j.status === 'processed' && j.applicationStatus === 'notApplied' },
  { key: 'applied', label: 'Applied', match: (j) => j.applicationStatus === 'applied' || j.applicationStatus === 'interview' },
  { key: 'offer', label: 'Offers', match: (j) => j.applicationStatus === 'offer' },
  { key: 'rejected', label: 'Rejected', match: (j) => j.applicationStatus === 'rejected' },
  { key: 'failed', label: 'Failed', match: (j) => j.status === 'failed' },
];

export function JobList() {
  const { jobs, loaded, error } = useJobs();
  const navigate = useNavigate();
  const [params, setParams] = useSearchParams();
  const active = filters.find((f) => f.key === params.get('filter')) ?? filters[0];

  const all = [...jobs.values()].sort((a, b) => b.capturedAt.localeCompare(a.capturedAt));
  const visible = all.filter(active.match);

  return (
    <>
      <Header />
      <main className="page">
        <SetupBanner />
        <PastePanel />

        <nav className="filters">
          {filters.map((f) => (
            <button
              key={f.key}
              className={f === active ? 'chip active' : 'chip'}
              onClick={() => setParams(f.key === 'all' ? {} : { filter: f.key })}
            >
              {f.label} <span className="count">{all.filter(f.match).length}</span>
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
                <th>Captured</th>
                <th>Role</th>
                <th>Company</th>
                <th>Location</th>
                <th>Processing</th>
                <th>Application</th>
              </tr>
            </thead>
            <tbody>
              {visible.map((job) => (
                <tr key={job.id} onClick={() => navigate(`/jobs/${job.id}`)}>
                  <td className="nowrap muted">{formatDateTime(job.capturedAt)}</td>
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
                  <td>
                    <ProcessingBadge status={job.status} />
                  </td>
                  <td className="nowrap">
                    <ApplicationBadge status={job.applicationStatus} />
                    {job.applicationStatus !== 'notApplied' && job.applicationStatusAt && (
                      <span className="muted small"> {formatDate(job.applicationStatusAt)}</span>
                    )}
                  </td>
                </tr>
              ))}
              {loaded && visible.length === 0 && (
                <tr>
                  <td colSpan={6} className="empty">
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

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const { id } = await api.paste(text, url);
      navigate(`/jobs/${id}`);
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
