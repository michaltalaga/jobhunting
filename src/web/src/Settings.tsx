import { useEffect, useState, type FormEvent } from 'react';
import { api, type SettingDef, type SettingValue, type TailoringSettings } from './api';
import { Header } from './ui';

let cached: Promise<TailoringSettings> | null = null;

/** The global tailoring settings, fetched once per page load (refreshed after a save). */
export function useTailoringSettings() {
  const [settings, setSettings] = useState<TailoringSettings | null>(null);
  useEffect(() => {
    cached ??= api.tailoringSettings();
    cached.then(setSettings, () => (cached = null));
  }, []);
  return settings;
}

/** One setting's input. An empty value means "not set" (inherit), shown with the inherited value as placeholder. */
export function SettingInput(props: {
  def: SettingDef;
  value: SettingValue | undefined;
  inherited?: SettingValue;
  onChange: (value: SettingValue | undefined) => void;
}) {
  const { def, value, inherited, onChange } = props;
  const placeholder = inherited === undefined || inherited === '' ? undefined : String(inherited);
  if (def.type === 'choice')
    return (
      <select value={value === undefined ? '' : String(value)} onChange={(e) => onChange(e.target.value || undefined)}>
        {inherited !== undefined && <option value="">Default ({String(inherited)})</option>}
        {def.options!.map((o) => (
          <option key={o} value={o}>
            {o}
          </option>
        ))}
      </select>
    );
  if (def.type === 'textarea')
    return (
      <textarea
        rows={3}
        value={value === undefined ? '' : String(value)}
        placeholder={placeholder}
        onChange={(e) => onChange(e.target.value === '' && inherited !== undefined ? undefined : e.target.value)}
      />
    );
  return (
    <input
      type={def.type === 'number' ? 'number' : 'text'}
      min={def.type === 'number' ? 0 : undefined}
      value={value === undefined ? '' : String(value)}
      placeholder={placeholder}
      onChange={(e) =>
        onChange(e.target.value === '' ? undefined : def.type === 'number' ? Number(e.target.value) : e.target.value)
      }
    />
  );
}

export function SettingsPage() {
  const loaded = useTailoringSettings();
  const [values, setValues] = useState<Record<string, SettingValue>>({});
  const [status, setStatus] = useState<string | null>(null);

  useEffect(() => {
    if (loaded) setValues(loaded.values);
  }, [loaded]);

  async function save(e: FormEvent) {
    e.preventDefault();
    try {
      const saved = await api.saveTailoringSettings(values);
      cached = Promise.resolve(saved);
      setValues(saved.values);
      setStatus('Saved. New tailoring runs use these settings; existing resumes are unchanged until you regenerate them.');
    } catch (err) {
      setStatus((err as Error).message);
    }
  }

  return (
    <>
      <Header />
      <main className="page narrow">
        <h1>Tailoring settings</h1>
        <p className="muted">
          Defaults for every job, saved in <code>data/tailoring.json</code>. Any job can override them on its own page.
        </p>
        {loaded && (
          <form className="card settings" onSubmit={save}>
            {loaded.definitions.map((def) => (
              <label key={def.key} className="setting">
                <span className="setting-label">{def.label}</span>
                <SettingInput
                  def={def}
                  value={values[def.key]}
                  onChange={(v) => setValues((prev) => ({ ...prev, [def.key]: v ?? def.default }))}
                />
                <span className="muted small">{def.description}</span>
              </label>
            ))}
            <div className="row">
              <button type="submit">Save</button>
              {status && <span className="muted small">{status}</span>}
            </div>
          </form>
        )}
      </main>
    </>
  );
}
