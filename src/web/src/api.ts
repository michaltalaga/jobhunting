export type ProcessingStatus = 'queued' | 'extracting' | 'tailoring' | 'rendering' | 'processed' | 'failed';
export type ApplicationStatus = 'notApplied' | 'applied' | 'interview' | 'rejected' | 'offer';

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
  pendingChanges: number;
  hasResume: boolean;
  hasPdf: boolean;
  duplicateOf: string[];
  updatedAt: string;
}

export interface ChangeRequest {
  at: string;
  text: string;
  status: 'pending' | 'done' | 'failed';
  error: string | null;
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
  spec: string | null;
  notes: string | null;
  resumeJson: string | null;
  changeRequests: ChangeRequest[];
  timeline: TimelineEvent[];
  runs: ClaudeRun[];
  warnings: string[];
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
  return response.status === 204 ? (undefined as T) : ((await response.json()) as T);
}

export const api = {
  setup: () => request<SetupStatus>('GET', '/setup'),
  list: () => request<JobSummary[]>('GET', '/jobs'),
  detail: (id: string) => request<JobDetail>('GET', `/jobs/${id}`),
  paste: (text: string, url: string) =>
    request<{ id: string; duplicateOf: string[] }>('POST', '/jobs/paste', { text, url: url || null }),
  requestChanges: (id: string, text: string) => request<JobSummary>('POST', `/jobs/${id}/changes`, { text }),
  retry: (id: string) => request<JobSummary>('POST', `/jobs/${id}/retry`),
  regenerate: (id: string) => request<JobSummary>('POST', `/jobs/${id}/regenerate`),
  reextract: (id: string) => request<JobSummary>('POST', `/jobs/${id}/reextract`),
  setApplicationStatus: (id: string, status: ApplicationStatus) =>
    request<JobSummary>('POST', `/jobs/${id}/application-status`, { status }),
  remove: (id: string) => request<void>('DELETE', `/jobs/${id}`),
};

/** Versioned by updatedAt so an iframe or link never shows a stale cached artifact. */
export const fileUrl = (job: JobSummary, name: string) =>
  `/api/jobs/${job.id}/files/${name}?v=${encodeURIComponent(job.updatedAt)}`;
