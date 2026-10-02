/**
 * The one resume layout every theme styles. Themes are CSS only (see themes/README.md), so the
 * document order below is also the order ATS parsers read: keep it plain, single-column HTML.
 * Class names are the theme contract; renaming one breaks every theme.
 */
import type { ReactNode } from 'react';

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
interface Volunteer extends Dated {
  organization?: string;
  position?: string;
  summary?: string;
  highlights?: string[];
}
interface Project extends Dated {
  name?: string;
  description?: string;
  highlights?: string[];
  keywords?: string[];
  roles?: string[];
}
interface Education extends Dated {
  institution?: string;
  area?: string;
  studyType?: string;
  score?: string;
}
export interface Resume {
  basics?: {
    name?: string;
    label?: string;
    email?: string;
    phone?: string;
    url?: string;
    summary?: string;
    location?: { city?: string; region?: string; countryCode?: string };
    profiles?: { network?: string; username?: string; url?: string }[];
  };
  work?: Work[];
  volunteer?: Volunteer[];
  education?: Education[];
  awards?: { title?: string; awarder?: string; date?: string; summary?: string }[];
  certificates?: { name?: string; issuer?: string; date?: string }[];
  publications?: { name?: string; publisher?: string; releaseDate?: string }[];
  skills?: { name?: string; level?: string; keywords?: string[] }[];
  languages?: { language?: string; fluency?: string }[];
  interests?: { name?: string; keywords?: string[] }[];
  projects?: Project[];
  /** Not in the JSON Resume schema: the advert's must-haves, each with one line of proof (a T-letter). */
  fit?: { requirement?: string; evidence?: string }[];
}

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

/** "2023-11-01" → "Nov 2023", "2023" → "2023". */
function month(date?: string) {
  if (!date) return '';
  const [year, m] = date.split('-');
  return m ? `${MONTHS[Number(m) - 1]} ${year}` : year;
}

function period({ startDate, endDate }: Dated) {
  if (!startDate && !endDate) return '';
  if (!startDate) return month(endDate);
  if (!endDate) return `${month(startDate)} – Present`;
  const [start, end] = [month(startDate), month(endDate)];
  return start === end ? start : `${start} – ${end}`; // "May 2026", not "May 2026 – May 2026"
}

/** Years only ("2011 – 2012"): enough for earlier roles and education, and no age signalling by month. */
function years({ startDate, endDate }: Dated) {
  const [start, end] = [startDate?.slice(0, 4), endDate?.slice(0, 4)];
  if (!start) return end ?? '';
  if (!end) return `${start} – Present`;
  return start === end ? start : `${start} – ${end}`;
}

/** Show "linkedin.com/in/jane" rather than the full URL: readable on paper and to ATS parsers. */
const bareUrl = (url: string) => url.replace(/^https?:\/\/(www\.)?/, '').replace(/\/$/, '');

function Section({ id, title, children }: { id: string; title: string; children: ReactNode }) {
  return (
    <section className={`r-section r-${id}`}>
      <h2>{title}</h2>
      {children}
    </section>
  );
}

function Entry(props: { title?: string; org?: string; location?: string; dates?: string; summary?: string; highlights?: string[]; keywords?: string[] }) {
  // An entry with nothing below its heading is a one-liner (typically a project): keywords go inline.
  const compact = !props.summary && !props.highlights?.length;
  const keywords = props.keywords?.length ? props.keywords.join(', ') : null;
  return (
    <div className={compact ? 'r-entry r-entry-compact' : 'r-entry'}>
      <div className="r-entry-head">
        <div className="r-entry-what">
          {props.title && <h3 className="r-title">{props.title}</h3>}
          {props.org && <span className="r-org">{props.org}</span>}
          {compact && keywords && <span className="r-keywords">{keywords}</span>}
        </div>
        <div className="r-entry-meta">
          {props.location && <span className="r-location">{props.location}</span>}
          {props.dates && <span className="r-dates">{props.dates}</span>}
        </div>
      </div>
      {props.summary && <p className="r-entry-summary">{props.summary}</p>}
      {props.highlights && props.highlights.length > 0 && (
        <ul className="r-highlights">
          {props.highlights.map((h, i) => (
            <li key={i}>{h}</li>
          ))}
        </ul>
      )}
      {!compact && keywords && <p className="r-keywords">{keywords}</p>}
    </div>
  );
}

export function ResumeDocument({ resume }: { resume: Resume }) {
  const { basics = {}, work = [], volunteer = [], education = [], projects = [], skills = [], languages = [], interests = [], fit = [] } = resume;
  const credentials = [
    ...(resume.certificates ?? []).map((c) => ({ title: c.name, by: c.issuer, date: c.date })),
    ...(resume.awards ?? []).map((a) => ({ title: a.title, by: a.awarder, date: a.date })),
  ];
  const location = [basics.location?.city, basics.location?.countryCode].filter(Boolean).join(', ');
  // Positions after the last one with highlights are earlier career: one line each, in their own section.
  const lastDetailed = work.map((w) => (w.highlights?.length ?? 0) > 0).lastIndexOf(true);
  const detailed = lastDetailed >= 0 ? work.slice(0, lastDetailed + 1) : work;
  const earlier = lastDetailed >= 0 ? work.slice(lastDetailed + 1) : [];

  return (
    <article className="resume">
      <header className="r-header">
        <h1 className="r-name">{basics.name}</h1>
        {basics.label && <p className="r-label">{basics.label}</p>}
        <ul className="r-contact">
          {basics.email && (
            <li className="r-email">
              <a href={`mailto:${basics.email}`}>{basics.email}</a>
            </li>
          )}
          {basics.phone && <li className="r-phone">{basics.phone}</li>}
          {location && <li className="r-location">{location}</li>}
          {basics.url && (
            <li className="r-url">
              <a href={basics.url}>{bareUrl(basics.url)}</a>
            </li>
          )}
          {(basics.profiles ?? [])
            .filter((p) => p.url)
            .map((p, i) => (
              <li key={i} className={`r-profile r-profile-${(p.network ?? '').toLowerCase()}`}>
                <a href={p.url}>{bareUrl(p.url!)}</a>
              </li>
            ))}
        </ul>
      </header>

      {basics.summary && (
        <Section id="summary" title="Summary">
          <p>{basics.summary}</p>
        </Section>
      )}

      {skills.length > 0 && (
        <Section id="skills" title="Skills">
          <ul className="r-skill-groups">
            {skills.map((s, i) => (
              <li key={i} className="r-skill-group">
                <span className="r-skill-name">{s.name}</span>
                {s.keywords && s.keywords.length > 0 && <span className="r-skill-keywords">{s.keywords.join(', ')}</span>}
              </li>
            ))}
          </ul>
        </Section>
      )}

      {/* With a fit block, page 1 ends here: print-base.css starts the next section on page 2. */}
      {fit.length > 0 && (
        <Section id="fit" title="Fit for This Role">
          <ul className="r-fit-list">
            {fit.map((f, i) => (
              <li key={i} className="r-fit-item">
                <span className="r-fit-requirement">{f.requirement}</span>
                <span className="r-fit-evidence">{f.evidence}</span>
              </li>
            ))}
          </ul>
        </Section>
      )}

      {detailed.length > 0 && (
        <Section id="work" title="Experience">
          {detailed.map((w, i) => (
            <Entry key={i} title={w.position} org={w.name ?? w.company} location={w.location} dates={period(w)} summary={w.summary} highlights={w.highlights} />
          ))}
        </Section>
      )}

      {earlier.length > 0 && (
        <Section id="earlier" title="Earlier Experience">
          <ul className="r-earlier-list">
            {earlier.map((w, i) => (
              <li key={i}>
                <span className="r-title">{w.position}</span>
                <span className="r-org">{w.name ?? w.company}</span>
                {w.location && <span className="r-location">{w.location}</span>}
                <span className="r-dates">{years(w)}</span>
                {w.summary && <span className="r-outcome">{w.summary}</span>}
              </li>
            ))}
          </ul>
        </Section>
      )}

      {projects.length > 0 && (
        <Section id="projects" title="Projects">
          {projects.map((p, i) => (
            <Entry key={i} title={p.name} org={p.description} dates={period(p)} highlights={p.highlights} keywords={p.keywords} />
          ))}
        </Section>
      )}

      {education.length > 0 && (
        <Section id="education" title="Education">
          {education.map((e, i) => (
            <Entry
              key={i}
              title={[e.studyType, e.area].filter(Boolean).join(', ')}
              org={e.institution}
              dates={(e.endDate ?? e.startDate)?.slice(0, 4)}
              summary={e.score}
            />
          ))}
        </Section>
      )}

      {credentials.length > 0 && (
        <Section id="certificates" title="Certifications & Awards">
          <ul className="r-list">
            {credentials.map((c, i) => (
              <li key={i}>
                <span className="r-item-title">{c.title}</span>
                {c.by && <span className="r-item-by">{c.by}</span>}
                {c.date && <span className="r-item-date">{month(c.date)}</span>}
              </li>
            ))}
          </ul>
        </Section>
      )}

      {(resume.publications ?? []).length > 0 && (
        <Section id="publications" title="Publications">
          <ul className="r-list">
            {resume.publications!.map((p, i) => (
              <li key={i}>
                <span className="r-item-title">{p.name}</span>
                {p.publisher && <span className="r-item-by">{p.publisher}</span>}
                {p.releaseDate && <span className="r-item-date">{month(p.releaseDate)}</span>}
              </li>
            ))}
          </ul>
        </Section>
      )}

      {volunteer.length > 0 && (
        <Section id="volunteer" title="Volunteering">
          {volunteer.map((v, i) => (
            <Entry key={i} title={v.position} org={v.organization} dates={period(v)} summary={v.summary} highlights={v.highlights} />
          ))}
        </Section>
      )}

      {languages.length > 0 && (
        <Section id="languages" title="Languages">
          <p className="r-inline-list">{languages.map((l) => [l.language, l.fluency && `(${l.fluency})`].filter(Boolean).join(' ')).join(' · ')}</p>
        </Section>
      )}

      {interests.length > 0 && (
        <Section id="interests" title="Interests">
          <p className="r-inline-list">{interests.map((x) => x.name).join(' · ')}</p>
        </Section>
      )}
    </article>
  );
}
