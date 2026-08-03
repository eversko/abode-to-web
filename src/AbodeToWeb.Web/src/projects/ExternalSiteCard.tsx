import { ExternalLink, Globe2, ServerCog } from 'lucide-react'
import { type SyntheticEvent, useState } from 'react'
import { useApi } from '../api/context'
import type { ExternalSiteSummary, SaveExternalSiteRequest } from '../api/schema'
import { Button } from '../components/Button'
import { CardBadge } from '../components/CardBadge'
import { StatusPill } from '../components/StatusPill'
import { formatRelativeTime, projectMonogram } from '../lib/format'

export function ExternalSiteCard({ site }: { site: ExternalSiteSummary }) {
  const host = new URL(site.url).hostname
  return (
    <article className="project-card external-site-card">
      <div className="project-card__head">
        <div className="project-identity">
          <span className="project-monogram" aria-hidden="true">{projectMonogram(site.name)}</span>
          <span><strong>{site.name}</strong><small>{site.description}</small></span>
        </div>
        <div className="project-card__head-actions">
          <CardBadge kind="external" />
          <StatusPill value={site.health} />
        </div>
      </div>
      <div className="project-card__meta">
        <div><span>Provider</span><strong>{site.provider}</strong></div>
        <div><span>Last checked</span><strong>{formatRelativeTime(site.observedAt)}</strong></div>
      </div>
      <a className="hostname-link" href={site.url} target="_blank" rel="noreferrer">{host}<ExternalLink aria-hidden="true" size={14} /></a>
      <div className="external-site-card__source"><span>Local source</span><strong title={site.repositoryPath ?? undefined}>{site.repositoryPath ?? 'Not linked'}</strong></div>
      {site.safeStatusMessage && <p className="external-site-card__message">{site.safeStatusMessage}</p>}
      <div className="project-card__actions">
        <a className="button button--quiet" href={site.url} target="_blank" rel="noreferrer"><ExternalLink aria-hidden="true" size={16} />Open site</a>
        {site.managementUrl && <a className="button button--primary" href={site.managementUrl} target="_blank" rel="noreferrer"><ServerCog aria-hidden="true" size={16} />Manage hosting</a>}
      </div>
    </article>
  )
}

const emptyRequest: SaveExternalSiteRequest = {
  name: '', description: null, url: 'https://', provider: '', managementUrl: null, repositoryPath: null,
}

export function ExternalSiteForm({ onSaved }: { onSaved: () => void }) {
  const api = useApi()
  const [siteId, setSiteId] = useState('')
  const [request, setRequest] = useState<SaveExternalSiteRequest>(emptyRequest)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string>()
  const update = <K extends keyof SaveExternalSiteRequest>(key: K, value: SaveExternalSiteRequest[K]) =>
    setRequest((current) => ({ ...current, [key]: value }))
  const save = async (event: SyntheticEvent<HTMLFormElement>) => {
    event.preventDefault()
    setSaving(true)
    setError(undefined)
    try {
      await api.saveExternalSite(siteId, request)
      onSaved()
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Abode to Web could not save the external site.')
    } finally {
      setSaving(false)
    }
  }

  return (
    <section className="panel external-site-form" aria-labelledby="external-site-form-title">
      <div className="panel__heading"><div><span className="eyebrow">Monitoring only</span><h3 id="external-site-form-title">Add an external site</h3></div><Globe2 aria-hidden="true" /></div>
      <form onSubmit={(event) => { void save(event) }}>
        <div className="form-grid">
          <label className="field"><span>Name</span><input required maxLength={80} value={request.name} onChange={(event) => update('name', event.target.value)} /></label>
          <label className="field"><span>Resource ID</span><input required pattern="[a-z][a-z0-9-]{0,62}" value={siteId} onChange={(event) => setSiteId(event.target.value)} placeholder="my-external-site" /></label>
        </div>
        <label className="field"><span>Description <small>optional</small></span><input maxLength={500} value={request.description ?? ''} onChange={(event) => update('description', event.target.value || null)} /></label>
        <div className="form-grid">
          <label className="field"><span>Public HTTPS URL</span><input required type="url" value={request.url} onChange={(event) => update('url', event.target.value)} /></label>
          <label className="field"><span>Hosting provider</span><input required maxLength={80} value={request.provider} onChange={(event) => update('provider', event.target.value)} placeholder="Spaceship / cPanel" /></label>
        </div>
        <div className="form-grid">
          <label className="field"><span>Management URL <small>optional</small></span><input type="url" value={request.managementUrl ?? ''} onChange={(event) => update('managementUrl', event.target.value || null)} /></label>
          <label className="field"><span>Local repository path <small>optional</small></span><input value={request.repositoryPath ?? ''} onChange={(event) => update('repositoryPath', event.target.value || null)} /></label>
        </div>
        <p className="field-hint">This records metadata and checks HTTPS reachability. It does not change DNS, proxy traffic, or enable remote deployment.</p>
        {error && <div className="inline-alert" role="alert">{error}</div>}
        <Button type="submit" variant="primary" disabled={saving}>{saving ? 'Saving…' : 'Save external site'}</Button>
      </form>
    </section>
  )
}
