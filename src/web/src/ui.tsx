import { Link } from 'react-router';
import type { ApplicationStatus, JobSummary, ProcessingStatus, TimelineEvent } from './api';
import { useJobs } from './jobs';

export const processingLabels: Record<ProcessingStatus, string> = {
  new: 'New',
  queued: 'In queue',
  extracting: 'Extracting spec',
  scoring: 'Scoring match',
  readyToTailor: 'Ready to tailor',
  tailoring: 'Tailoring resume',
  rendering: 'Rendering PDF',
  reviewing: 'Reviewing',
  processed: 'Processed',
  failed: 'Failed',
  onHold: 'On hold',
};

export const applicationLabels: Record<ApplicationStatus, string> = {
  notApplied: 'Not applied',
  applied: 'Applied',
  interview: 'Interview',
  rejected: 'Rejected',
  offer: 'Offer',
  dropped: 'Dropped',
};

export const applicationOrder: ApplicationStatus[] = ['notApplied', 'applied', 'interview', 'rejected', 'offer', 'dropped'];

export const isWorking = (s: ProcessingStatus) =>
  s === 'extracting' || s === 'scoring' || s === 'tailoring' || s === 'rendering' || s === 'reviewing';

/** Application log event types; `response` = the employer came back to you. Mirrors ApplicationEventTypes on the server. */
export const applicationEventTypes: { type: string; label: string; response: boolean }[] = [
  { type: 'confirmed', label: 'They confirmed', response: true },
  { type: 'contacted', label: 'Recruiter reached out', response: true },
  { type: 'interviewScheduled', label: 'Interview scheduled', response: true },
  { type: 'interviewDone', label: 'Interview done', response: true },
  { type: 'followedUp', label: 'I followed up', response: false },
  { type: 'rejected', label: 'Rejected', response: true },
  { type: 'offer', label: 'Offer', response: true },
  { type: 'note', label: 'Note', response: false },
];

export const eventLabel = (type: string) => applicationEventTypes.find((t) => t.type === type)?.label ?? type;

/** Applied, and the employer hasn't responded since: "no reply · 12d". */
export function NoReplyTag({ job }: { job: JobSummary }) {
  if (!isAwaitingReply(job)) return null;
  const days = Math.floor((Date.now() - Date.parse(job.appliedAt!)) / 86_400_000);
  return (
    <span className={days >= 14 ? 'tag error' : 'tag warn'} title="Applied, and no response logged since">
      no reply · {days}d
    </span>
  );
}

// Timestamps may carry different offsets ("Z" vs "+02:00"), so compare them as dates, not strings.
export const isAwaitingReply = (job: JobSummary) =>
  job.applicationStatus === 'applied' &&
  !!job.appliedAt &&
  !(job.lastResponseAt && Date.parse(job.lastResponseAt) >= Date.parse(job.appliedAt));

/** Match score as a coloured pill: green 80+, amber 60–79, red below 60. */
export function MatchPill({ score }: { score: number | null }) {
  if (score == null) return <span className="muted">—</span>;
  const band = score >= 80 ? 'high' : score >= 60 ? 'mid' : 'low';
  return (
    <span className={`match match-${band}`} title="How well your profile fits this job">
      {score}
    </span>
  );
}

export function ProcessingBadge({ status }: { status: ProcessingStatus }) {
  return (
    <span className={`badge processing-${status}`}>
      {isWorking(status) && <span className="spinner" aria-hidden />}
      {processingLabels[status]}
    </span>
  );
}

export function ApplicationBadge({ status }: { status: ApplicationStatus }) {
  return <span className={`badge application-${status}`}>{applicationLabels[status]}</span>;
}

const dateTime = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' });
const dateOnly = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium' });

export const formatDateTime = (iso: string) => dateTime.format(new Date(iso));
export const formatDate = (iso: string) => dateOnly.format(new Date(iso));

/** A yyyy-MM-dd calendar date, read as local (new Date('2026-10-09') would be UTC midnight). */
const localDay = (day: string) => {
  const [y, m, d] = day.split('-').map(Number);
  return new Date(y, m - 1, d);
};
export const formatDay = (day: string) => dateOnly.format(localDay(day));

/** Whole days from today until a yyyy-MM-dd date (negative once it has passed). */
export const daysUntil = (day: string) => {
  const today = new Date();
  today.setHours(0, 0, 0, 0);
  return Math.round((localDay(day).getTime() - today.getTime()) / 86_400_000);
};

const timelineProcessing: Record<ProcessingStatus, string> = {
  new: 'Captured',
  queued: 'Queued',
  extracting: 'Extraction started',
  scoring: 'Match scoring started',
  readyToTailor: 'Ready to tailor',
  tailoring: 'Tailoring started',
  rendering: 'PDF rendering started',
  reviewing: 'Pre-send review started',
  processed: 'Processing finished',
  failed: 'Processing failed',
  onHold: 'Put on hold',
};

export function timelineLabel(e: TimelineEvent) {
  const [kind, value] = e.event.split('.');
  switch (kind) {
    case 'captured':
      return 'Captured from the browser';
    case 'pasted':
      return 'Pasted as text';
    case 'processing':
      return timelineProcessing[value as ProcessingStatus] ?? e.event;
    case 'application':
      return `Marked “${applicationLabels[value as ApplicationStatus] ?? value}”`;
    case 'change':
      return 'Change requested';
    case 'retried':
      return 'Retried';
    case 'regenerated':
      return 'Regeneration requested';
    case 'reextracted':
      return 'Re-extraction requested';
    case 'theme':
      return 'Theme changed';
    case 'tailoring':
      return 'Tailoring overrides changed';
    case 'reviewed':
      return 'Reviewed by the HR consultant';
    case 'matched':
      return 'Match scored';
    case 'tailor':
      return 'Tailoring requested';
    case 'held':
      return 'Taken out of the queue';
    case 'stopped':
      return 'Stopped while running';
    case 'resumed':
      return 'Resumed';
    default:
      return e.event;
  }
}

export function ClosingTag({ job }: { job: JobSummary }) {
  if (!job.closingDate || job.applicationStatus !== 'notApplied') return null;
  const days = daysUntil(job.closingDate);
  const text = days < 0 ? 'closed' : days === 0 ? 'closes today' : `closes ${formatDay(job.closingDate)}`;
  return (
    <span className={days <= 3 ? 'tag error' : 'tag'} title={`Application deadline ${job.closingDate}`}>
      {text}
    </span>
  );
}

export const jobTitle = (job: JobSummary) => job.role ?? job.pageTitle ?? (job.status === 'failed' ? 'Unrecognised capture' : 'Extracting…');

export function Header() {
  const { connected } = useJobs();
  return (
    <header className="topbar">
      <Link to="/" className="brand">
        Job Hunting
      </Link>
      <nav className="topnav">
        <Link to="/settings">Settings</Link>
        <span className={`live ${connected ? 'on' : 'off'}`} title={connected ? 'Live updates connected' : 'Server not reachable'}>
          {connected ? 'Live' : 'Offline'}
        </span>
      </nav>
    </header>
  );
}
