# Resume themes

A theme is a folder with a stylesheet:

```
themes/<id>/
  theme.css     required
  theme.json    { "name": "…", "description": "…" }
  …             any fonts or images theme.css uses, referenced with relative url()s
```

Built-in themes live here. Put your own in `data/themes/<id>/`. A personal theme with the same id as a built-in one replaces it. Themes appear in the picker on each job's page; the default is `JobHunting:Render:Theme`.

The quickest start is to copy `classic/` or `modern/` into `data/themes/<new-id>/` and edit it. Preview it on any job's page, where changes show as soon as you reload. Picking the theme there re-renders that job's PDF.

## The page

Every theme styles the same HTML, rendered from the tailored `resume.json` by `src/web/src/ResumeDocument.tsx`. The page size (A4) and margins (14 mm × 16 mm) come from `src/web/src/print-base.css`. A theme can override them with its own `@page` rule.

When the resume has a fit block, `print-base.css` also makes page 1 the pitch page: the section after `.r-fit` starts on page 2. To let the content flow on instead, set `.r-fit + .r-section { break-before: auto; }` in your theme.

Keep it **single column** and leave the document order alone. ATS parsers read the PDF's text in document order, and multi-column layouts or reordered sections can scramble it. Everything on the page is real text, so a theme should not hide content or use `content:` to add words that matter.

## Class names

```
article.resume
  header.r-header
    h1.r-name
    p.r-label
    ul.r-contact
      li.r-email  li.r-phone  li.r-location  li.r-url  li.r-profile.r-profile-<network>
  section.r-section.r-<id>          id: summary, skills, fit, work, earlier, projects, education,
    h2                                  certificates, publications, volunteer, languages, interests
    p                                summary
    ul.r-skill-groups                skills
      li.r-skill-group
        span.r-skill-name  span.r-skill-keywords
    ul.r-fit-list                    fit ("Fit for This Role"): the advert's must-haves, each with its proof
      li.r-fit-item
        span.r-fit-requirement  span.r-fit-evidence
    div.r-entry                      work, projects, education, volunteer
      div.r-entry-head
        div.r-entry-what   h3.r-title  span.r-org  [span.r-keywords]
        div.r-entry-meta   span.r-location  span.r-dates
      p.r-entry-summary
      ul.r-highlights > li
      p.r-keywords                   projects
    div.r-entry.r-entry-compact      an entry with no summary or highlights (a one-line project):
                                     its keywords sit inline as span.r-keywords in .r-entry-what
    ul.r-earlier-list > li           earlier: positions after the last one with highlights, one line each
      span.r-title  span.r-org  span.r-location  span.r-dates (years)  span.r-outcome
    ul.r-list > li                   certificates (certificates + awards), publications
      span.r-item-title  span.r-item-by  span.r-item-date
    p.r-inline-list                  languages, interests
```

Any element may be missing when the resume has no data for it.
