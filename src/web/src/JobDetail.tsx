import { useEffect, useState, type FormEvent } from 'react';
import { Link, useNavigate, useParams } from 'react-router';
import Markdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import { api, ApiError, fileUrl, type JobDetail, type JobSummary } from './api';
import { useJobs } from './jobs';
import { ResumePreview } from './ResumePreview';
import {
  ClosingTag,
  Header,
  ProcessingBadge,
  applicationLabels,
  applicationOrder,
  formatDate,
  formatDateTime,
  formatDay,
  isWorking,
  jobTitle,
  timelineLabel,
} from './ui';

type Tab = 'resume' | 'pdf' | 'notes' | 'spec' | 'json' | 'capture' | 'runs';

export function JobDetailPage() {
  const { id = '' } = useParams();
  const { jobs } = useJobs();
  const live = jobs.get(id);
  const [detail, setDetail] = useState<JobDetail | null>(null);
  const [missing, setMissing] = useState(false);

  // Refetch whenever the live summary says the job changed.
  const version = live?.updatedAt;
  useEffect(() => {
    let cancelled = false;
    api
      .detail(id)
      .then((d) => !cancelled && (setDetail(d), setMissing(false)))
      .catch((e) => !cancelled && e instanceof ApiError && e.status === 404 && setMissing(true));
    return () => {
      cancelled = true;
    };
  }, [id, version]);

  if (missing)
    return (
      <>
        <Header />
        <main className="page">
          <p className="empty">
            This job no longer exists. <Link to="/">Back to all jobs</Link>
          </p>
        </main>
      </>
    );
  if (!detail) return <Header />;

  const job = live ?? detail.job;
  return (
    <>
      <Header />
      <main className="page detail">
        <Link to="/" className="back">
          ← All jobs
        </Link>
        <JobHeading job={job} detail={detail} />
        <div className="detail-grid">
          <Artifacts job={job} detail={detail} />
          <aside>
            <ProcessingCard job={job} />
            <ApplicationCard job={job} />
            <ChangesCard job={job} detail={detail} />
            <TimelineCard detail={detail} />
            <DangerCard job={job} />
          </aside>
        </div>
      </main>
    </>
  );
}

function JobHeading({ job, detail }: { job: JobSummary; detail: JobDetail }) {
  const recruiter = detail.recruiter;
  return (
    <div className="heading">
      <h1>
        {jobTitle(job)} <ClosingTag job={job} />
      </h1>
      <p className="subtitle">{[job.company, job.location].filter(Boolean).join(' · ') || 'Company not identified yet'}</p>
      {recruiter && (
        <p className="recruiter">
          <span className="muted">Recruiter:</span> <strong>{recruiter.name}</strong>
          {recruiter.title && <span className="muted"> · {recruiter.title}</span>}
          {recruiter.profileUrl && (
            <a href={recruiter.profileUrl} target="_blank" rel="noreferrer">
              Profile ↗
            </a>
          )}
          {recruiter.email && <a href={`mailto:${recruiter.email}`}>{recruiter.email}</a>}
          {recruiter.phone && <a href={`tel:${recruiter.phone}`}>{recruiter.phone}</a>}
        </p>
      )}
      <p className="meta">
        <span>
          {job.source === 'paste' ? 'Pasted' : 'Captured'} {formatDateTime(job.capturedAt)}
        </span>
        {job.postedDate && <span>Posted {formatDay(job.postedDate)}</span>}
        {job.closingDate && <span>Closes {formatDay(job.closingDate)}</span>}
        {job.url && (
          <a href={job.url} target="_blank" rel="noreferrer">
            Original advert ↗
          </a>
        )}
        <code title="Folder in the repository">applications/{job.folder}</code>
      </p>
      {job.duplicateOf.length > 0 && (
        <p className="alert warn">
          This URL was captured more than once:{' '}
          {job.duplicateOf.map((d) => (
            <Link key={d} to={`/jobs/${d}`}>
              {d}
            </Link>
          ))}
        </p>
      )}
    </div>
  );
}

function Artifacts({ job, detail }: { job: JobSummary; detail: JobDetail }) {
  const available: { key: Tab; label: string; show: boolean }[] = [
    { key: 'resume', label: 'Resume', show: !!detail.resumeJson },
    { key: 'pdf', label: 'PDF', show: job.hasPdf },
    { key: 'notes', label: 'Notes', show: !!detail.notes },
    { key: 'spec', label: 'Job spec', show: !!detail.spec },
    { key: 'json', label: 'JSON', show: !!detail.resumeJson },
    { key: 'capture', label: 'Capture', show: true },
    { key: 'runs', label: 'Runs', show: detail.runs.length > 0 },
  ];
  const tabs = available.filter((t) => t.show);
  const [chosen, setChosen] = useState<Tab | null>(null);
  const tab = tabs.find((t) => t.key === chosen)?.key ?? tabs[0].key;

  return (
    <section className="card artifacts">
      <nav className="tabs">
        {tabs.map((t) => (
          <button key={t.key} className={t.key === tab ? 'tab active' : 'tab'} onClick={() => setChosen(t.key)}>
            {t.label}
          </button>
        ))}
      </nav>

      {detail.warnings.length > 0 && (tab === 'resume' || tab === 'pdf' || tab === 'json') && (
        <div className="alert warn">
          <strong>Check these against your master resume:</strong>
          <ul>
            {detail.warnings.map((w, i) => (
              <li key={i}>{w}</li>
            ))}
          </ul>
        </div>
      )}

      {tab === 'resume' && detail.resumeJson && <ResumePreview json={detail.resumeJson} />}
      {tab === 'pdf' && <iframe className="pdf" src={fileUrl(job, 'resume.pdf')} title="Resume PDF" />}
      {tab === 'notes' && <Markdown remarkPlugins={[remarkGfm]}>{detail.notes ?? ''}</Markdown>}
      {tab === 'spec' && <Markdown remarkPlugins={[remarkGfm]}>{detail.spec ?? ''}</Markdown>}
      {tab === 'json' && <JsonView json={detail.resumeJson ?? ''} job={job} />}
      {tab === 'capture' && (
        <>
          <p className="row">
            <a href={fileUrl(job, 'page.txt')} target="_blank" rel="noreferrer">
              Captured text ↗
            </a>
            {job.source === 'extension' && (
              <a href={fileUrl(job, 'page.html')} target="_blank" rel="noreferrer">
                Captured HTML ↗
              </a>
            )}
          </p>
          <iframe className="capture" src={fileUrl(job, 'page.txt')} title="Captured text" />
        </>
      )}
      {tab === 'runs' && <Runs detail={detail} />}
    </section>
  );
}

function JsonView({ json, job }: { json: string; job: JobSummary }) {
  const [copied, setCopied] = useState(false);
  return (
    <>
      <p className="row">
        <button
          className="secondary"
          onClick={() => navigator.clipboard.writeText(json).then(() => setCopied(true))}
        >
          {copied ? 'Copied' : 'Copy JSON'}
        </button>
        <a href={fileUrl(job, 'resume.json')} target="_blank" rel="noreferrer">
          Open raw ↗
        </a>
      </p>
      <pre className="json">{json}</pre>
    </>
  );
}

function Runs({ detail }: { detail: JobDetail }) {
  const total = detail.runs.reduce((sum, r) => sum + (r.costUsd ?? 0), 0);
  return (
    <table className="runs">
      <thead>
        <tr>
          <th>Step</th>
          <th>When</th>
          <th>Duration</th>
          <th>Model</th>
          <th>Cost</th>
        </tr>
      </thead>
      <tbody>
        {detail.runs.map((r, i) => (
          <tr key={i}>
            <td>{r.step}</td>
            <td className="nowrap">{formatDateTime(r.at)}</td>
            <td>{Math.round(r.durationMs / 1000)} s</td>
            <td className="muted">
              {r.model} · {r.effort}
            </td>
            <td>{r.costUsd == null ? '—' : `$${r.costUsd.toFixed(2)}`}</td>
          </tr>
        ))}
      </tbody>
      <tfoot>
        <tr>
          <td colSpan={4}>Total (API-equivalent cost)</td>
          <td>${total.toFixed(2)}</td>
        </tr>
      </tfoot>
    </table>
  );
}

function ProcessingCard({ job }: { job: JobSummary }) {
  const { upsert } = useJobs();
  const [error, setError] = useState<string | null>(null);
  return (
    <section className="card">
      <h2>Processing</h2>
      <p>
        <ProcessingBadge status={job.status} />
      </p>
      {job.status === 'queued' && <p className="muted small">Waiting for the worker. Jobs run one at a time.</p>}
      {isWorking(job.status) && <p className="muted small">Claude is working on it. This can take several minutes.</p>}
      {job.error && <p className="alert error">{job.error}</p>}
      {job.status === 'failed' && (
        <button onClick={() => api.retry(job.id).then(upsert, (e: Error) => setError(e.message))}>Retry</button>
      )}
      {error && <p className="alert error">{error}</p>}
    </section>
  );
}

function ApplicationCard({ job }: { job: JobSummary }) {
  const { upsert } = useJobs();
  const [error, setError] = useState<string | null>(null);
  return (
    <section className="card">
      <h2>Application</h2>
      <div className="segmented">
        {applicationOrder.map((s) => (
          <button
            key={s}
            className={s === job.applicationStatus ? `active application-${s}` : ''}
            onClick={() => api.setApplicationStatus(job.id, s).then(upsert, (e: Error) => setError(e.message))}
          >
            {applicationLabels[s]}
          </button>
        ))}
      </div>
      {error && <p className="alert error">{error}</p>}
      {job.applicationStatusAt && (
        <p className="muted small">
          {applicationLabels[job.applicationStatus]} since {formatDate(job.applicationStatusAt)}
          {job.appliedAt && job.applicationStatus !== 'applied' && <> · applied {formatDate(job.appliedAt)}</>}
        </p>
      )}
    </section>
  );
}

function TimelineCard({ detail }: { detail: JobDetail }) {
  return (
    <section className="card">
      <h2>Timeline</h2>
      <ol className="timeline">
        {detail.timeline
          .slice()
          .reverse()
          .map((e, i) => (
            <li key={i} className={e.event.startsWith('application.') ? 'milestone' : undefined}>
              <span className="when">{formatDateTime(e.at)}</span>
              <span>{timelineLabel(e)}</span>
              {e.detail && e.event !== 'captured' && e.event !== 'pasted' && <span className="detail">{e.detail}</span>}
            </li>
          ))}
      </ol>
    </section>
  );
}

function ChangesCard({ job, detail }: { job: JobSummary; detail: JobDetail }) {
  const { upsert } = useJobs();
  const [text, setText] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setBusy(true);
    setError(null);
    try {
      upsert(await api.requestChanges(job.id, text));
      setText('');
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setBusy(false);
    }
  }

  return (
    <section className="card">
      <h2>Request changes</h2>
      {job.applicationStatus !== 'notApplied' && (
        <p className="alert warn small">
          You've already applied with the current version. A change overwrites it, so commit it first if you want to keep it.
        </p>
      )}
      <form onSubmit={submit}>
        <textarea
          rows={4}
          placeholder="e.g. Emphasise the payments work more and drop the side projects."
          value={text}
          onChange={(e) => setText(e.target.value)}
        />
        <button type="submit" disabled={busy || !text.trim()}>
          {busy ? 'Queuing…' : 'Queue change'}
        </button>
      </form>
      {error && <p className="alert error">{error}</p>}
      {detail.changeRequests.length > 0 && (
        <ul className="history">
          {detail.changeRequests
            .slice()
            .reverse()
            .map((c, i) => (
              <li key={i}>
                <span className={`tag ${c.status === 'failed' ? 'error' : c.status === 'pending' ? '' : 'ok'}`}>{c.status}</span>{' '}
                {c.text} <span className="muted">{formatDateTime(c.at)}</span>
                {c.error && <div className="small error-text">{c.error}</div>}
              </li>
            ))}
        </ul>
      )}
    </section>
  );
}

function DangerCard({ job }: { job: JobSummary }) {
  const { upsert } = useJobs();
  const navigate = useNavigate();
  const [error, setError] = useState<string | null>(null);
  const busy = isWorking(job.status);

  return (
    <section className="card">
      <h2>More</h2>
      <div className="row">
        <button
          className="secondary"
          disabled={busy || !job.hasResume}
          title="Discard the tailored resume and notes, then tailor again from the master resume"
          onClick={() =>
            confirm('Discard the current resume and tailor it again from scratch?') &&
            api.regenerate(job.id).then(upsert, (e: Error) => setError(e.message))
          }
        >
          Regenerate
        </button>
        <button
          className="secondary"
          disabled={busy}
          title="Extract the spec, recruiter and dates from the capture again. The tailored resume is kept."
          onClick={() => api.reextract(job.id).then(upsert, (e: Error) => setError(e.message))}
        >
          Re-extract
        </button>
        <button
          className="danger"
          disabled={busy}
          onClick={() =>
            confirm(`Delete this job and its folder applications/${job.folder}?`) &&
            api.remove(job.id).then(() => navigate('/'), (e: Error) => setError(e.message))
          }
        >
          Delete
        </button>
      </div>
      {error && <p className="alert error">{error}</p>}
    </section>
  );
}
