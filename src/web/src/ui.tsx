import { Link } from 'react-router';
import type { ApplicationStatus, JobSummary, ProcessingStatus, TimelineEvent } from './api';
import { useJobs } from './jobs';

export const processingLabels: Record<ProcessingStatus, string> = {
  queued: 'In queue',
  extracting: 'Extracting spec',
  tailoring: 'Tailoring resume',
  rendering: 'Rendering PDF',
  processed: 'Processed',
  failed: 'Failed',
};

export const applicationLabels: Record<ApplicationStatus, string> = {
  notApplied: 'Not applied',
  applied: 'Applied',
  interview: 'Interview',
  rejected: 'Rejected',
  offer: 'Offer',
};

export const applicationOrder: ApplicationStatus[] = ['notApplied', 'applied', 'interview', 'rejected', 'offer'];

export const isWorking = (s: ProcessingStatus) => s === 'extracting' || s === 'tailoring' || s === 'rendering';

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
  queued: 'Queued',
  extracting: 'Extraction started',
  tailoring: 'Tailoring started',
  rendering: 'PDF rendering started',
  processed: 'Processing finished',
  failed: 'Processing failed',
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
      <span className={`live ${connected ? 'on' : 'off'}`} title={connected ? 'Live updates connected' : 'Server not reachable'}>
        {connected ? 'Live' : 'Offline'}
      </span>
    </header>
  );
}
