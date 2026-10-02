// print.html?job=<id>&theme=<theme>: the resume alone, styled by a theme.
// The job page shows it in a frame, and the server prints it to resume.pdf with headless Chrome,
// so the preview and the PDF are the same page.
import { flushSync } from 'react-dom';
import { createRoot } from 'react-dom/client';
import type { JobDetail } from './api';
import { ResumeDocument, type Resume } from './ResumeDocument';
import './print-base.css';

const params = new URLSearchParams(location.search);
const jobId = params.get('job') ?? '';
const theme = params.get('theme') ?? 'classic';

function loadTheme() {
  const link = document.createElement('link');
  link.rel = 'stylesheet';
  link.href = `/themes/${encodeURIComponent(theme)}/theme.css`;
  const loaded = new Promise((resolve) => (link.onload = link.onerror = resolve));
  document.head.appendChild(link);
  return loaded;
}

async function main() {
  const themeLoaded = loadTheme();
  const response = await fetch(`/api/jobs/${encodeURIComponent(jobId)}`);
  if (!response.ok) throw new Error(`job ${jobId} not found`);
  const detail = (await response.json()) as JobDetail;
  if (!detail.resumeJson) throw new Error('this job has no tailored resume yet');
  const resume = JSON.parse(detail.resumeJson) as Resume;

  // Chrome uses the title as the PDF's document title.
  document.title = [resume.basics?.name, 'CV'].filter(Boolean).join(' – ');
  flushSync(() => createRoot(document.getElementById('root')!).render(<ResumeDocument resume={resume} />));
  await themeLoaded;
  await document.fonts.ready;
  document.documentElement.dataset.ready = 'true';
}

main().catch((e: Error) => {
  document.body.textContent = `Can't show the resume: ${e.message}`;
});
