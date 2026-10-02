export type ProcessingStatus =
  | 'new'
  | 'queued'
  | 'extracting'
  | 'scoring'
  | 'readyToTailor'
  | 'tailoring'
  | 'rendering'
  | 'reviewing'
  | 'processed'
  | 'failed'
  | 'onHold';

export interface ApplicationEvent {
  id: string;
  at: string;
  type: string;
  note: string | null;
}

export interface MatchRequirement {
  requirement: string;
  status: 'met' | 'partial' | 'missing';
  evidence: string | null;
}

export interface JobMatch {
  score: number;
  summary: string | null;
  requirements: MatchRequirement[];
  seniority: string | null;
  domain: string | null;
  location: string | null;
  biggestGap: string | null;
  at: string;
}

export type RenderState = 'none' | 'queued' | 'rendering' | 'failed';

export interface QueueState {
  paused: boolean;
  /** Jobs the workers are running right now (several when Workers > 1). */
  running: string[];
  waiting: string[];
}
export type ApplicationStatus = 'notApplied' | 'applied' | 'interview' | 'rejected' | 'offer' | 'dropped';

export interface JobSummary {
  id: string;
  folder: string;
  source: 'extension' | 'paste';
  url: string | null;
  pageTitle: string | null;
  company: string | null;
  role: string | null;
  location: string | null;
  recruiterName: string | null;
  capturedAt: string;
  /** yyyy-MM-dd */
  postedDate: string | null;
  /** yyyy-MM-dd */
  closingDate: string | null;
  status: ProcessingStatus;
  error: string | null;
  applicationStatus: ApplicationStatus;
  applicationStatusAt: string | null;
  appliedAt: string | null;
  /** Latest logged employer response (confirmation, contact, interview, rejection, offer). */
  lastResponseAt: string | null;
  pendingChanges: number;
  /** null: the default theme */
  theme: string | null;
  matchScore: number | null;
  hasResume: boolean;
  hasPdf: boolean;
  pdfPages: number | null;
  /** The theme the current PDF was rendered with. */
  pdfTheme: string | null;
  /** PDFs render only when you ask, in their own queue. */
  render: RenderState;
  renderError: string | null;
  duplicateOf: string[];
  updatedAt: string;
}

export interface ChangeRequest {
  at: string;
  text: string;
  status: 'pending' | 'done' | 'failed';
  error: string | null;
  /** Written by the pre-send review, not by you; hidden in the UI. */
  fromReview: boolean;
}

export interface Recruiter {
  name: string | null;
  title: string | null;
  profileUrl: string | null;
  email: string | null;
  phone: string | null;
}

/** event: "captured" | "pasted" | "processing.<status>" | "application.<status>" | "change.requested" | "retried" | "regenerated" | "reextracted" */
export interface TimelineEvent {
  at: string;
  event: string;
  detail: string | null;
}

export interface ClaudeRun {
  step: string;
  at: string;
  durationMs: number;
  costUsd: number | null;
  model: string;
  effort: string;
}

export interface JobDetail {
  job: JobSummary;
  recruiter: Recruiter | null;
  match: JobMatch | null;
  applicationEvents: ApplicationEvent[];
  /** Only the settings this job overrides. */
  tailoringOverrides: Record<string, SettingValue>;
  spec: string | null;
  notes: string | null;
  resumeJson: string | null;
  changeRequests: ChangeRequest[];
  timeline: TimelineEvent[];
  runs: ClaudeRun[];
  warnings: string[];
}

export type SettingValue = string | number;

export interface SettingDef {
  key: string;
  label: string;
  type: 'number' | 'choice' | 'text' | 'textarea';
  default: SettingValue;
  description: string;
  options: string[] | null;
}

export interface TailoringSettings {
  definitions: SettingDef[];
  values: Record<string, SettingValue>;
}

export interface ThemeInfo {
  id: string;
  name: string;
  description: string | null;
  personal: boolean;
}

export interface ThemeList {
  default: string;
  themes: ThemeInfo[];
}

export interface SetupStatus {
  problems: string[];
  masterResumePath: string;
  backgroundFiles: string[];
  claudeVersion: string | null;
}

export class ApiError extends Error {
  constructor(message: string, readonly status: number) {
    super(message);
  }
}

async function request<T>(method: string, path: string, body?: unknown): Promise<T> {
  const response = await fetch(`/api${path}`, {
    method,
    headers: body === undefined ? undefined : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!response.ok) {
    const text = await response.text();
    let message = text || `${response.status} ${response.statusText}`;
    try {
      const parsed = JSON.parse(text);
      message = typeof parsed === 'string' ? parsed : parsed.title ?? parsed.detail ?? message;
    } catch {
      // plain-text error body
    }
    throw new ApiError(message, response.status);
  }
  const text = await response.text(); // 202/204 replies have no body
  return (text ? JSON.parse(text) : undefined) as T;
}

export const api = {
  setup: () => request<SetupStatus>('GET', '/setup'),
  queue: () => request<QueueState>('GET', '/queue'),
  pauseQueue: () => request<QueueState>('POST', '/queue/pause'),
  resumeQueue: () => request<QueueState>('POST', '/queue/resume'),
  hold: (id: string) => request<JobSummary | undefined>('POST', `/jobs/${id}/hold`),
  resume: (id: string) => request<JobSummary>('POST', `/jobs/${id}/resume`),
  prioritize: (id: string) => request<QueueState>('POST', `/jobs/${id}/prioritize`),
  tailor: (id: string) => request<JobSummary>('POST', `/jobs/${id}/tailor`),
  addEvent: (id: string, type: string, note: string, at: string) =>
    request<JobSummary>('POST', `/jobs/${id}/events`, { type, note: note || null, at }),
  deleteEvent: (id: string, eventId: string) => request<JobSummary>('DELETE', `/jobs/${id}/events/${eventId}`),
  score: (id: string) => request<JobSummary>('POST', `/jobs/${id}/score`),
  list: () => request<JobSummary[]>('GET', '/jobs'),
  detail: (id: string) => request<JobDetail>('GET', `/jobs/${id}`),
  /** created = false: the URL was already in the list; nothing was added and id is the existing job. */
  paste: (text: string, url: string) =>
    request<{ id: string; created: boolean; duplicateOf: string[] }>('POST', '/jobs/paste', { text, url: url || null }),
  requestChanges: (id: string, text: string) => request<JobSummary>('POST', `/jobs/${id}/changes`, { text }),
  retry: (id: string) => request<JobSummary>('POST', `/jobs/${id}/retry`),
  regenerate: (id: string) => request<JobSummary>('POST', `/jobs/${id}/regenerate`),
  reextract: (id: string) => request<JobSummary>('POST', `/jobs/${id}/reextract`),
  themes: () => request<ThemeList>('GET', '/themes'),
  tailoringSettings: () => request<TailoringSettings>('GET', '/settings/tailoring'),
  saveTailoringSettings: (values: Record<string, SettingValue>) =>
    request<TailoringSettings>('PUT', '/settings/tailoring', values),
  setJobTailoring: (id: string, overrides: Record<string, SettingValue>, regenerate: boolean) =>
    request<JobSummary>('PUT', `/jobs/${id}/tailoring`, { overrides, regenerate }),
  setTheme: (id: string, theme: string) => request<JobSummary>('POST', `/jobs/${id}/theme`, { theme }),
  render: (id: string) => request<JobSummary>('POST', `/jobs/${id}/render`),
  setApplicationStatus: (id: string, status: ApplicationStatus) =>
    request<JobSummary>('POST', `/jobs/${id}/application-status`, { status }),
  remove: (id: string) => request<void>('DELETE', `/jobs/${id}`),
};

/** Versioned by updatedAt so an iframe or link never shows a stale cached artifact. */
export const fileUrl = (job: JobSummary, name: string) =>
  `/api/jobs/${job.id}/files/${name}?v=${encodeURIComponent(job.updatedAt)}`;

/** The same page the server prints to resume.pdf. */
export const printUrl = (job: JobSummary, theme: string) =>
  `/print.html?job=${job.id}&theme=${encodeURIComponent(theme)}&v=${encodeURIComponent(job.updatedAt)}`;
