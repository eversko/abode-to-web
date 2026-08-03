import { GitCommitHorizontal, Search } from 'lucide-react'
import { useCallback, useMemo, useState } from 'react'
import { Link } from 'react-router'
import { useApi } from '../api/context'
import { DeployTimeline } from '../components/DeployTimeline'
import { ErrorState, LoadingState } from '../components/ResourceState'
import { StatusPill } from '../components/StatusPill'
import { useResource } from '../hooks/useResource'
import { formatDateTime } from '../lib/format'

export function DeploymentsPage() {
  const api = useApi()
  const load = useCallback(async (signal: AbortSignal) => {
    const [deployments, projects] = await Promise.all([api.listDeployments(undefined, signal), api.listProjects(signal)])
    return { deployments, projects }
  }, [api])
  const { data, error, loading, reload } = useResource(load)
  const [query, setQuery] = useState('')
  const deployments = useMemo(() => data?.deployments.filter((deployment) => {
    const name = data.projects.find((project) => project.id === deployment.projectId)?.name ?? deployment.projectId
    return `${name} ${deployment.shortSha} ${deployment.branch} ${deployment.commitMessage ?? ''}`.toLowerCase().includes(query.toLowerCase())
  }) ?? [], [data, query])

  return <div className="page-stack"><section className="page-intro"><div><span className="eyebrow">Release history</span><h2>Deployments</h2><p>Every candidate build, health result, route switch, and retained release.</p></div></section><div className="toolbar"><label className="search-field"><Search aria-hidden="true" size={17} /><span className="sr-only">Search deployments</span><input type="search" placeholder="Search deployments" value={query} onChange={(event) => setQuery(event.target.value)} /></label><span>{deployments.length} deployment{deployments.length === 1 ? '' : 's'}</span></div>
    {loading && !data ? <LoadingState label="Loading deployments" /> : error && !data ? <ErrorState error={error} retry={reload} /> : <section className="panel"><div className="table-scroll"><table className="data-table data-table--roomy"><thead><tr><th scope="col">Project</th><th scope="col">Commit</th><th scope="col">Status</th><th scope="col">Outcome</th><th scope="col">Completed</th><th scope="col"><span className="sr-only">Open</span></th></tr></thead><tbody>{deployments.map((deployment) => { const project = data?.projects.find((item) => item.id === deployment.projectId); return <tr key={deployment.id}><td><span className="table-project-icon" aria-hidden="true"><GitCommitHorizontal /></span><strong>{project?.name ?? deployment.projectId}</strong></td><td><strong>{deployment.commitMessage ?? 'Deployment'}</strong><span><code>{deployment.shortSha}</code> · {deployment.branch}</span></td><td><StatusPill value={deployment.status} /><DeployTimeline deployment={deployment} variant="inline" /></td><td><span>{deployment.buildSucceeded ? 'Build passed' : 'Build failed'}</span><span>{deployment.healthSucceeded ? 'Health passed' : 'Health not passed'}</span></td><td>{formatDateTime(deployment.completedAt ?? deployment.createdAt)}</td><td><Link className="text-link" to={`/projects/${deployment.projectId}/deployments`}>Details</Link></td></tr> })}</tbody></table></div></section>}
  </div>
}
