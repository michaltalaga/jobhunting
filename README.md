# jobhunting

Tailors your resume to every job advert you find, and keeps a record of each application.

1. On a job advert in Chrome, click the **Job Hunting Capture** button. The page is captured exactly as rendered (SPAs and embedded iframes included) and queued.
2. A local worker runs the headless Claude CLI:
   - **extract**: pulls the advert out of the page noise, along with company, role, location, recruiter, posted date and closing date;
   - **tailor**: writes a resume for that job from your master resume (plus optional extra career notes), and notes on fit, keyword coverage, gaps and claims to double-check.
3. A dashboard at <http://localhost:5317> lists every job with its processing and application status. Each job's page shows the spec, the tailored resume and a timeline. You can request changes, and track applied → interview → rejected / offer.

Everything runs on your machine. Your data stays in `data/`, which this repo ignores.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) and [Node.js](https://nodejs.org) 20+
- [Claude Code](https://claude.com/claude-code) CLI, logged in (run `claude` once). The default model is Opus 5.5, which needs CLI 2.1.280 or newer.
- Chrome

## Setup

1. Clone this repo.
2. Put your resume in [JSON Resume](https://jsonresume.org/schema) format at `data/resume.json`.
3. Optional: put any extra material about your career in `data/background/` as `.md` or `.txt` files: project and achievement notes with numbers, older or longer CVs, a LinkedIn export, performance reviews. Tailoring draws on all of it, but never invents anything beyond it and your resume. The folder is created on first run, with a note explaining it.
4. Start the server:
   ```bash
   dotnet run --project src/Server
   ```
   The first run also installs and builds the web UI. Open <http://localhost:5317>. If anything is missing, the dashboard says what.
5. Install the capture button: open `chrome://extensions`, enable **Developer mode**, choose **Load unpacked**, and select `src/extension`. Pin it to the toolbar.

## Use

- **Capture:** click the button on a job advert. The badge shows ✓ when the job is queued, `dup` when that URL was captured before, and ✗ when the server isn't running. Expand any "show more" section first, because only text that's on the page gets captured.
- **No web page?** Use **Paste a job advert** on the dashboard.
- **Adjust the result:** on a job's page, describe what to change; the job goes back into the queue. **Regenerate** tailors from scratch; **Re-extract** re-reads the capture.
- **Track it:** set the application status as things progress. Every event is timestamped in the job's timeline.

Jobs run one at a time. Extraction takes under a minute, and tailoring a few minutes.

## Your data

```
data/                                  ignored by this repo
  resume.json                          master resume (JSON Resume), the authority on titles and dates
  background/                          optional: any .md/.txt files with extra career material
  settings.json                        optional personal overrides (see Configuration)
  applications/
    _inbox/<id>/                       until extraction knows company and role
    2026-10-01-acme-staff-engineer/    afterwards (-2, -3 … if the name is taken)
      job.json      state and timeline: captured, each processing step, application status changes
      page.txt      captured visible text (all frames)
      page.html     captured HTML, minus scripts/styles/SVGs
      spec.md       the extracted advert
      resume.json   tailored resume (overwritten by change requests)
      notes.md      fit, keyword coverage, gaps, things to check
```

The folders are the source of truth. The server rebuilds its index from them on start, and resumes any job that was interrupted.

To keep history of your applications, make `data/` its own **private** git repository:

```bash
cd data
git init
git remote add origin <your private repo URL>
```

## Guard rails

The tailoring prompt forbids inventing employers, titles, dates, metrics or skills. It may select, reorder and rephrase, mirroring the advert's terminology where that's truthful. After every run the server also:

- copies contact details from the master resume over whatever the model wrote;
- flags any work or education entry whose employer, title or dates don't match the master resume. These warnings show on the job's page.

## Configuration

Defaults live in `src/Server/appsettings.json`, under `JobHunting`. To override them, use an optional `data/settings.json` with the same shape:

```json
{
  "JobHunting": {
    "Claude": { "Tailor": { "Effort": "high" } }
  }
}
```

| Setting | Default | |
|---|---|---|
| `MasterResumePath` | `data/resume.json` | paths are relative to the repo root |
| `BackgroundDir` | `data/background` | |
| `ApplicationsDir` | `data/applications` | |
| `Claude:Extract`, `Claude:Tailor` | `claude-opus-5-5`, effort `xhigh` | model and effort for each step |
| `Claude:TimeoutMinutes` | `30` | per CLI call |

Environment variables override both files, e.g. `JobHunting__Claude__Tailor__Effort=high`.

## Layout

```
src/Server/      ASP.NET Core 10: API, queue worker, Claude engine; serves the web UI
src/web/         React + Vite dashboard (dev: `npm run dev` on :5173, proxies to :5317)
src/extension/   Chrome extension: the capture button
prompts/         extract.md, tailor.md: the engine's instructions
tool/            PDF rendering (work in progress)
```
