# jobhunting

Tailors your resume to every job advert you find, and keeps a record of each application.

1. On a job advert in Chrome, click the **Job Hunting Capture** button. The page is captured exactly as rendered (SPAs and embedded iframes included) and queued.
2. A local worker runs the headless Claude CLI:
   - **extract**: pulls the advert out of the page noise, along with company, role, location, recruiter, posted date and closing date;
   - **tailor**: writes a resume for that job from your master resume (plus optional extra career notes), and notes on fit, keyword coverage, gaps and claims to double-check.
3. The resume is printed to PDF with headless Chrome, using a theme you pick per job.
4. A dashboard at <http://localhost:5317> lists every job with its processing and application status. Each job's page shows the spec, the resume exactly as the PDF will look, and a timeline. You can request changes, and track applied → interview → rejected / offer.

Everything runs on your machine. Your data stays in `data/`, which this repo ignores.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download) and [Node.js](https://nodejs.org) 20+
- [Claude Code](https://claude.com/claude-code) CLI, logged in (run `claude` once). The default model is Opus 5.5, which needs CLI 2.1.280 or newer.
- Chrome (or Edge): for the capture button, and to print PDFs

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

- **Capture:** click the button on a job advert. The badge shows ✓ when the job is queued, `dup` when that URL is already in your list (nothing is added; delete the old job first to capture it again), and ✗ when the server isn't running. Expand any "show more" section first, because only text that's on the page gets captured.
- **No web page?** Use **Paste a job advert** on the dashboard.
- **Nothing runs on its own:** a capture lands as **New**. **Score** extracts the advert and gives a **match score** (0–100: how well your profile fits, with must-haves met/partial/missing and the biggest gap), then waits at **Ready to tailor**. Click **Tailor** on the jobs worth it; sort the list by **Match** to pick. (Setting `tailorAutomatically` makes tailoring follow scoring.)
- **Manage the queue:** the strip at the top of the job list shows the running job (with **Stop**) and the waiting ones in order (**Run next**, **Hold**). **Pause queue** lets the running job finish and starts nothing new until you resume. Held or stopped jobs keep everything done so far and wait for **Resume** on their page.
- **Adjust the result:** on a job's page, describe what to change; the job goes back into the queue. **Regenerate** tailors from scratch; **Re-extract** re-reads the capture.
- **Pick a look:** the theme picker on a job's page re-renders its PDF in seconds, without calling Claude. **Download PDF** saves it as `Your_Name_CV.pdf`. Two themes ship in [`themes/`](themes/README.md); add your own in `data/themes/`.
- **Tune the tailoring:** **Settings** sets the defaults for every job: length (target and limit in pages), the **Fit for This Role** block (the advert's top must-haves, each with one line of proof, which with the summary and skills fills page 1 so experience starts on page 2), how many years and roles get full detail, bullets per role, how earlier roles appear, how many projects and awards, and free-text instructions. Each job's **Tailoring** card can override any of them for that job. A job page warns when its PDF exceeds the page limit.
- **Track it:** set the application status as things progress, and log what happens in the job's Application card: *They confirmed*, *Recruiter reached out*, *Interview scheduled/done*, *I followed up*, *Rejected*, *Offer*, or a note, each with a date. Applied jobs where the employer hasn't responded since are tagged **no reply · Nd**, and the **No reply** filter lists them.

Jobs run one at a time. Extraction takes under a minute, and tailoring a few minutes.

## Your data

```
data/                                  ignored by this repo
  resume.json                          master resume (JSON Resume), the authority on titles and dates
  background/                          optional: any .md/.txt files with extra career material
  tailoring.json                       tailoring settings (created on first run; edit here or on the Settings page)
  settings.json                        optional server overrides (see Configuration)
  applications/
    _inbox/<id>/                       until extraction knows company and role
    2026-10-01-acme-staff-engineer/    afterwards (-2, -3 … if the name is taken)
      job.json      state and timeline: captured, each processing step, application status changes
      page.txt      captured visible text (all frames)
      page.html     captured HTML, minus scripts/styles/SVGs
      spec.md       the extracted advert
      resume.json   tailored resume (overwritten by change requests)
      resume.pdf    the resume printed with the job's theme
      notes.md      fit, keyword coverage, gaps, things to check
  themes/                              optional: your own PDF themes
```

The folders are the source of truth. The server rebuilds its index from them on start, and resumes any job that was interrupted.

The server log is written to `data/logs/server-YYYY-MM-DD.log` as well as the console: each step, and each Claude call with its duration and cost.

To keep history of your applications, make `data/` its own **private** git repository:

```bash
cd data
git init
git remote add origin <your private repo URL>
```

## Guard rails

The tailoring prompt forbids inventing employers, titles, dates, metrics or skills. It may select, reorder and rephrase, mirroring the advert's terminology where that's truthful.

The model sees the master resume as Markdown, with every entry tagged by a reference (`[W3]` is the third work entry), and picks entries by that reference. After every run the server:

- copies contact details and each picked entry's facts from the master resume over whatever the model wrote: employers, titles, locations, dates, degrees, project names, certificate and award names. The model only writes the text;
- writes the closing "More projects" line itself, with the real count;
- flags every number in the text that appears nowhere in your master resume, background files, instructions or change requests, and any entry it can't find in the master resume.

The warnings show on the job's page, and the pre-send review must fix each flagged number.

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
| `Claude:TimeoutMinutes` | `20` | per CLI call; a stuck call is killed and the job marked Failed |
| `Workers` | `4` | how many jobs are processed at the same time |
| `Render:Theme` | `classic` | default PDF theme |
| `Render:ChromePath` | found automatically | Chrome or Edge executable |

Environment variables override both files, e.g. `JobHunting__Claude__Tailor__Effort=high`.

## Layout

```
src/Server/      ASP.NET Core 10: API, queue worker, Claude engine, PDF rendering; serves the web UI
src/web/         React + Vite: the dashboard, and print.html (the resume page the PDF is printed from)
src/extension/   Chrome extension: the capture button
prompts/         extract.md, tailor.md: the engine's instructions
themes/          built-in PDF themes (CSS), and how to write your own
```
