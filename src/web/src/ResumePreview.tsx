/** A plain HTML reading view of a JSON Resume, independent of the PDF theme. */

interface Dated {
  startDate?: string;
  endDate?: string;
}
interface Work extends Dated {
  name?: string;
  company?: string;
  position?: string;
  location?: string;
  summary?: string;
  highlights?: string[];
}
interface Project extends Dated {
  name?: string;
  description?: string;
  highlights?: string[];
  keywords?: string[];
}
interface Resume {
  basics?: { name?: string; label?: string; summary?: string; email?: string; phone?: string };
  work?: Work[];
  projects?: Project[];
  skills?: { name?: string; keywords?: string[] }[];
  education?: (Dated & { institution?: string; area?: string; studyType?: string })[];
  awards?: { title?: string; awarder?: string; date?: string }[];
  certificates?: { name?: string; issuer?: string; date?: string }[];
  languages?: { language?: string; fluency?: string }[];
}

const period = ({ startDate, endDate }: Dated) =>
  startDate ? `${startDate.slice(0, 7)} – ${endDate ? endDate.slice(0, 7) : 'present'}` : '';

export function ResumePreview({ json }: { json: string }) {
  let resume: Resume;
  try {
    resume = JSON.parse(json) as Resume;
  } catch {
    return <p className="alert error">resume.json is not valid JSON.</p>;
  }
  const { basics = {}, work = [], projects = [], skills = [], education = [], awards = [], certificates = [], languages = [] } = resume;

  return (
    <article className="resume">
      <h1>{basics.name}</h1>
      {basics.label && <p className="label">{basics.label}</p>}
      {basics.summary && <p>{basics.summary}</p>}

      {skills.length > 0 && (
        <section>
          <h2>Skills</h2>
          <dl className="skills">
            {skills.map((s, i) => (
              <div key={i}>
                <dt>{s.name}</dt>
                <dd>{s.keywords?.join(', ')}</dd>
              </div>
            ))}
          </dl>
        </section>
      )}

      {work.length > 0 && (
        <section>
          <h2>Experience</h2>
          {work.map((w, i) => (
            <div key={i} className="entry">
              <div className="entry-head">
                <strong>{w.position}</strong> · {w.name ?? w.company}
                <span className="muted">{period(w)}</span>
              </div>
              {w.summary && <p>{w.summary}</p>}
              {w.highlights && w.highlights.length > 0 && (
                <ul>
                  {w.highlights.map((h, j) => (
                    <li key={j}>{h}</li>
                  ))}
                </ul>
              )}
            </div>
          ))}
        </section>
      )}

      {projects.length > 0 && (
        <section>
          <h2>Projects</h2>
          {projects.map((p, i) => (
            <div key={i} className="entry">
              <div className="entry-head">
                <strong>{p.name}</strong>
                {p.description && <> · {p.description}</>}
                <span className="muted">{period(p)}</span>
              </div>
              {p.highlights && p.highlights.length > 0 && (
                <ul>
                  {p.highlights.map((h, j) => (
                    <li key={j}>{h}</li>
                  ))}
                </ul>
              )}
              {p.keywords && p.keywords.length > 0 && <p className="muted small">{p.keywords.join(', ')}</p>}
            </div>
          ))}
        </section>
      )}

      {education.length > 0 && (
        <section>
          <h2>Education</h2>
          {education.map((e, i) => (
            <div key={i} className="entry-head">
              <span>
                <strong>{[e.studyType, e.area].filter(Boolean).join(', ')}</strong> · {e.institution}
              </span>
              <span className="muted">{period(e)}</span>
            </div>
          ))}
        </section>
      )}

      {(awards.length > 0 || certificates.length > 0) && (
        <section>
          <h2>Certifications &amp; awards</h2>
          <ul>
            {certificates.map((c, i) => (
              <li key={`c${i}`}>
                {c.name} · {c.issuer} <span className="muted">{c.date?.slice(0, 7)}</span>
              </li>
            ))}
            {awards.map((a, i) => (
              <li key={`a${i}`}>
                {a.title} · {a.awarder} <span className="muted">{a.date?.slice(0, 7)}</span>
              </li>
            ))}
          </ul>
        </section>
      )}

      {languages.length > 0 && (
        <section>
          <h2>Languages</h2>
          <p>{languages.map((l) => `${l.language} (${l.fluency})`).join(' · ')}</p>
        </section>
      )}
    </article>
  );
}
