import { useEffect, useState, type FormEvent } from 'react';
import { Link, useNavigate, useParams } from 'react-router';
import Markdown from 'react-markdown';
import remarkGfm from 'remark-gfm';
import { api, ApiError, fileUrl, printUrl, type JobDetail, type JobSummary, type SettingValue, type ThemeList } from './api';
import { useJobs } from './jobs';
import { QueueControls } from './Queue';
import { SettingInput, useTailoringSettings } from './Settings';
import {
  ClosingTag,
  Header,
  MatchPill,
  NoReplyTag,
  ProcessingBadge,
  applicationEventTypes,
  eventLabel,
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
            <MatchCard job={job} detail={detail} />
            <ProcessingCard job={job} />
            <ApplicationCard job={job} detail={detail} />
            <ChangesCard job={job} detail={detail} />
            <TailoringCard job={job} detail={detail} />
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
          <strong>Check these before sending:</strong>
          <ul>
            {detail.warnings.map((w, i) => (
              <li key={i}>{w}</li>
            ))}
          </ul>
        </div>
      )}

      {(tab === 'resume' || tab === 'pdf') && <ResumeToolbar job={job} />}
      {tab === 'resume' && <ResumeFrame job={job} />}
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

let themeList: Promise<ThemeList> | null = null;

function useThemes() {
  const [themes, setThemes] = useState<ThemeList | null>(null);
  useEffect(() => {
    themeList ??= api.themes();
    themeList.then(setThemes, () => (themeList = null));
  }, []);
  return themes;
}

/** The job's theme, or the default when it hasn't picked one (or picked one that no longer exists). */
function useJobTheme(job: JobSummary) {
  const themes = useThemes();
  const theme = themes?.themes.some((t) => t.id === job.theme) ? job.theme! : themes?.default;
  return { themes, theme };
}

function ResumeToolbar({ job }: { job: JobSummary }) {
  const { upsert } = useJobs();
  const { themes, theme } = useJobTheme(job);
  const [error, setError] = useState<string | null>(null);
  const current = themes?.themes.find((t) => t.id === theme);
  const pdfStale = job.hasPdf && !!theme && job.pdfTheme !== null && job.pdfTheme !== theme;
  const pending = job.render === 'queued' || job.render === 'rendering';
  // Render only when there's a resume that isn't being rewritten and no render is already pending.
  const canRender = job.hasResume && !isWorking(job.status) && !pending && (!job.hasPdf || pdfStale || job.render === 'failed');

  return (
    <div className="resume-toolbar">
      <label>
        Theme{' '}
        <select
          value={theme ?? ''}
          disabled={!themes}
          onChange={(e) => api.setTheme(job.id, e.target.value).then(upsert, (err: Error) => setError(err.message))}
        >
          {themes?.themes.map((t) => (
            <option key={t.id} value={t.id}>
              {t.name}
              {t.personal ? ' (yours)' : ''}
            </option>
          ))}
        </select>
      </label>
      {current?.description && <span className="muted small">{current.description}</span>}
      <span className="spacer" />
      {job.render === 'queued' && <span className="muted small">Queued for PDF…</span>}
      {job.render === 'rendering' && <span className="muted small">Rendering PDF…</span>}
      {!pending && !job.hasPdf && job.render !== 'failed' && <span className="muted small">No PDF yet</span>}
      {pdfStale && !pending && <span className="muted small">PDF uses the {job.pdfTheme} theme</span>}
      {canRender && (
        <button className={job.hasPdf ? 'secondary' : ''} onClick={() => api.render(job.id).then(upsert, (err: Error) => setError(err.message))}>
          {job.hasPdf ? 'Render again' : 'Render PDF'}
        </button>
      )}
      {job.hasPdf && (
        <a className="button" href={`${fileUrl(job, 'resume.pdf')}&download=true`}>
          Download PDF{job.pdfPages ? ` (${job.pdfPages} pp)` : ''}
        </a>
      )}
      {job.render === 'failed' && job.renderError && <p className="alert error">PDF failed: {job.renderError}</p>}
      {error && <p className="alert error">{error}</p>}
    </div>
  );
}

/** The print page the PDF is made from, so what you see here is what gets sent. */
function ResumeFrame({ job }: { job: JobSummary }) {
  const { theme } = useJobTheme(job);
  if (!theme) return null;
  return <iframe className="preview" src={printUrl(job, theme)} title="Resume preview" />;
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

const requirementMark = { met: '✓', partial: '~', missing: '✗' } as const;

/** How well your profile fits this advert, scored right after extraction. */
function MatchCard({ job, detail }: { job: JobSummary; detail: JobDetail }) {
  const { upsert } = useJobs();
  const [error, setError] = useState<string | null>(null);
  const match = detail.match;
  // New jobs get Score in the Processing card instead.
  const busy = isWorking(job.status) || job.status === 'queued' || job.status === 'new';

  return (
    <section className="card">
      <div className="match-head">
        <h2>Match</h2>
        <MatchPill score={match?.score ?? null} />
      </div>
      {!match && (
        <p className="muted small">{job.status === 'scoring' ? 'Scoring your profile against the advert…' : 'Not scored yet.'}</p>
      )}
      {match && (
        <>
          {match.summary && <p className="small">{match.summary}</p>}
          {match.biggestGap && (
            <p className="small">
              <strong>Biggest gap:</strong> {match.biggestGap}
            </p>
          )}
          <ul className="requirements">
            {match.requirements.map((r, i) => (
              <li key={i} className={`req-${r.status}`} title={r.evidence ?? undefined}>
                <span className="req-mark">{requirementMark[r.status] ?? '?'}</span>
                <span>
                  {r.requirement}
                  {r.evidence && <span className="muted"> · {r.evidence}</span>}
                </span>
              </li>
            ))}
          </ul>
          <dl className="fit">
            {match.seniority && (
              <>
                <dt>Seniority</dt>
                <dd>{match.seniority}</dd>
              </>
            )}
            {match.domain && (
              <>
                <dt>Domain &amp; tech</dt>
                <dd>{match.domain}</dd>
              </>
            )}
            {match.location && (
              <>
                <dt>Location</dt>
                <dd>{match.location}</dd>
              </>
            )}
          </dl>
        </>
      )}
      {!busy && (
        <button className="secondary small" onClick={() => api.score(job.id).then(upsert, (e: Error) => setError(e.message))}>
          {match ? 'Re-score' : 'Score match'}
        </button>
      )}
      {error && <p className="alert error">{error}</p>}
    </section>
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
      {isWorking(job.status) && <p className="muted small">Claude is working on it. This can take several minutes.</p>}
      {(job.status === 'new' || (job.status === 'onHold' && !job.hasResume)) && (
        <>
          <p className="muted small">
            {job.status === 'new' ? 'Captured. Nothing runs until you start it: ' : 'On hold. Start it again with '}
            <strong>Score</strong> extracts the advert and scores your match (a few minutes); <strong>Tailor</strong> does that
            and then writes the resume.
          </p>
          <div className="row">
            {job.matchScore == null && (
              <button onClick={() => api.score(job.id).then(upsert, (e: Error) => setError(e.message))}>Score</button>
            )}
            <button className="secondary" onClick={() => api.tailor(job.id).then(upsert, (e: Error) => setError(e.message))}>
              Tailor
            </button>
          </div>
        </>
      )}
      {job.status === 'readyToTailor' && (
        <>
          <p className="muted small">Scored and waiting for you. Tailoring takes 10–20 minutes, including the pre-send review.</p>
          <button onClick={() => api.tailor(job.id).then(upsert, (e: Error) => setError(e.message))}>Tailor</button>
        </>
      )}
      <QueueControls job={job} />
      {job.error && <p className="alert error">{job.error}</p>}
      {job.status === 'failed' && (
        <button onClick={() => api.retry(job.id).then(upsert, (e: Error) => setError(e.message))}>Retry</button>
      )}
      {error && <p className="alert error">{error}</p>}
    </section>
  );
}

const today = () => new Date().toLocaleDateString('sv-SE'); // yyyy-MM-dd in local time

/** Log what happens after you apply, so you can see later who confirmed, who called, and who never came back. */
function ApplicationLog({ job, detail }: { job: JobSummary; detail: JobDetail }) {
  const { upsert } = useJobs();
  const [type, setType] = useState<string | null>(null);
  const [note, setNote] = useState('');
  const [date, setDate] = useState(today());
  const [error, setError] = useState<string | null>(null);

  function open(t: string) {
    setType(t);
    setNote('');
    setDate(today());
    setError(null);
  }

  async function save(e: FormEvent) {
    e.preventDefault();
    if (!type) return;
    try {
      // Noon local time, so the date survives any time-zone conversion.
      upsert(await api.addEvent(job.id, type, note, new Date(`${date}T12:00:00`).toISOString()));
      setType(null);
    } catch (err) {
      setError((err as Error).message);
    }
  }

  return (
    <div className="app-log">
      <div className="event-buttons">
        {applicationEventTypes.map((t) => (
          <button key={t.type} className={type === t.type ? 'chip active' : 'chip'} onClick={() => open(t.type)}>
            {t.label}
          </button>
        ))}
      </div>
      {type && (
        <form className="event-form" onSubmit={save}>
          <strong className="small">{eventLabel(type)}</strong>
          <input type="date" value={date} max={today()} onChange={(e) => setDate(e.target.value)} />
          <textarea
            rows={2}
            placeholder={type === 'note' ? 'Note' : 'Note (optional): who, how, what they said'}
            value={note}
            onChange={(e) => setNote(e.target.value)}
            autoFocus
          />
          <div className="row">
            <button type="submit" disabled={type === 'note' && !note.trim()}>
              Log it
            </button>
            <button type="button" className="secondary" onClick={() => setType(null)}>
              Cancel
            </button>
          </div>
        </form>
      )}
      {error && <p className="alert error">{error}</p>}
      {detail.applicationEvents.length > 0 && (
        <ul className="history">
          {detail.applicationEvents
            .slice()
            .reverse()
            .map((ev) => (
              <li key={ev.id} className="event">
                <span>
                  <strong>{eventLabel(ev.type)}</strong> <span className="muted">{formatDate(ev.at)}</span>
                  {ev.note && <div className="small">{ev.note}</div>}
                </span>
                <button
                  className="link-button"
                  title="Delete this entry"
                  onClick={() => api.deleteEvent(job.id, ev.id).then(upsert, (err: Error) => setError(err.message))}
                >
                  ×
                </button>
              </li>
            ))}
        </ul>
      )}
    </div>
  );
}

function ApplicationCard({ job, detail }: { job: JobSummary; detail: JobDetail }) {
  const { upsert } = useJobs();
  const [error, setError] = useState<string | null>(null);
  return (
    <section className="card">
      <h2>
        Application <NoReplyTag job={job} />
      </h2>
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
      <ApplicationLog job={job} detail={detail} />
    </section>
  );
}

/** This job's overrides of the global tailoring settings. Blank fields inherit the global value. */
function TailoringCard({ job, detail }: { job: JobSummary; detail: JobDetail }) {
  const { upsert } = useJobs();
  const settings = useTailoringSettings();
  const saved = JSON.stringify(detail.tailoringOverrides);
  const [overrides, setOverrides] = useState<Record<string, SettingValue>>(detail.tailoringOverrides);
  const [error, setError] = useState<string | null>(null);
  useEffect(() => setOverrides(JSON.parse(saved)), [saved]);

  const count = Object.keys(detail.tailoringOverrides).length;
  const dirty = JSON.stringify(overrides) !== saved;

  function set(key: string, value: SettingValue | undefined) {
    setOverrides((prev) => {
      const next = { ...prev };
      if (value === undefined) delete next[key];
      else next[key] = value;
      return next;
    });
  }

  function save(regenerate: boolean) {
    setError(null);
    api.setJobTailoring(job.id, overrides, regenerate).then(upsert, (e: Error) => setError(e.message));
  }

  return (
    <section className="card">
      <details>
        <summary>
          <h2>Tailoring</h2>
          <span className="muted small">{count === 0 ? 'global settings' : `${count} override${count > 1 ? 's' : ''}`}</span>
        </summary>
        {settings?.definitions.map((def) => (
          <label key={def.key} className="setting compact" title={def.description}>
            <span className="setting-label">{def.label}</span>
            <SettingInput def={def} value={overrides[def.key]} inherited={settings.values[def.key]} onChange={(v) => set(def.key, v)} />
          </label>
        ))}
        <div className="row">
          <button className="secondary" disabled={!dirty} onClick={() => save(false)}>
            Save
          </button>
          <button disabled={isWorking(job.status) || (!dirty && count === 0)} onClick={() => save(true)}>
            Save &amp; regenerate
          </button>
        </div>
        <p className="muted small">Blank fields use the global settings. Changes apply to the next tailoring run.</p>
        {error && <p className="alert error">{error}</p>}
      </details>
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
      {detail.changeRequests.some((c) => !c.fromReview) && (
        <ul className="history">
          {detail.changeRequests
            .filter((c) => !c.fromReview)
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
