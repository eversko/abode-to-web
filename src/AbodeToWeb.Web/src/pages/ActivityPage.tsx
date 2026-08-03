import { Check, CircleAlert, Clock3, ShieldX } from 'lucide-react'
import { useCallback } from 'react'
import { Link } from 'react-router'
import { useApi } from '../api/context'
import { ErrorState, LoadingState } from '../components/ResourceState'
import { useResource } from '../hooks/useResource'
import { formatDateTime } from '../lib/format'

export function ActivityPage() {
  const api = useApi()
  const load = useCallback((signal: AbortSignal) => api.listActivity(signal), [api])
  const { data, error, loading, reload } = useResource(load)
  return <div className="page-stack"><section className="page-intro"><div><span className="eyebrow">Audit trail</span><h2>Activity</h2><p>Administrative actions and their safe outcomes. Secret values are never included.</p></div></section>
    {loading && !data ? <LoadingState label="Loading activity" /> : error && !data ? <ErrorState error={error} retry={reload} /> : <section className="panel activity-feed" aria-label="Administrative activity">{data?.map((event) => { const Icon = event.outcome === 'succeeded' ? Check : event.outcome === 'failed' ? CircleAlert : event.outcome === 'rejected' ? ShieldX : Clock3; return <article className="activity-item" key={event.sequence} data-outcome={event.outcome}><span className="activity-item__icon"><Icon aria-hidden="true" /></span><div><div className="activity-item__title"><strong>{event.action}</strong><span>{formatDateTime(event.timestamp)}</span></div><p>{event.summary}</p><small>{event.actor}{event.projectId && <> · <Link to={`/projects/${event.projectId}`}>{event.projectId}</Link></>}</small></div></article> })}</section>}
  </div>
}
