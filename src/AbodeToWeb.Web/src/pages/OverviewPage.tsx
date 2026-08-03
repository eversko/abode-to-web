import { ArrowRight, Boxes, ChevronDown, Cpu, HardDrive, MemoryStick, Rocket, ShieldCheck } from 'lucide-react'
import { useCallback } from 'react'
import { Link } from 'react-router'
import { useApi } from '../api/context'
import type { ComponentStatus, DeploymentSummary, SystemStatus } from '../api/schema'
import { Button } from '../components/Button'
import { ErrorState, LoadingState } from '../components/ResourceState'
import { StatusPill } from '../components/StatusPill'
import { useResource } from '../hooks/useResource'
import { formatBytes, formatPercent, formatRelativeTime } from '../lib/format'
import { useOperations } from '../operations/OperationContext'
import { ExternalSiteCard } from '../projects/ExternalSiteCard'
import { LocalProjectCard } from '../projects/LocalProjectCard'
import { ProjectCard } from '../projects/ProjectCard'

export function OverviewPage() {
  const api = useApi()
  const { runOperation } = useOperations()
  const load = useCallback((signal: AbortSignal) =>
    Promise.all([
      api.getOverview(signal),
      api.listExternalSites(signal).catch(() => []),
      api.listLocalProjects(signal).catch(() => []),
    ]).then(([overview, externalSites, localProjects]) => ({ ...overview, externalSites, localProjects })), [api])
  const { data, error, loading, reload } = useResource(load)

  if (loading && !data) return <LoadingState label="Loading your hosting overview" />
  if (error && !data) return <ErrorState error={error} retry={reload} />
  if (!data) return null

  const failedDeployments = data.recentDeployments.filter((deployment) => deployment.status === 'failed')
  const outdated = data.projects.filter((project) => project.git?.updateAvailable)
  const deployAllUpdates = () => {
    for (const project of outdated) {
      void runOperation(api.runProjectAction(project.id, 'deploy'), `Deploy ${project.name}`, reload)
    }
  }

  return (
    <div className="page-stack">
      <section className="hero-panel">
        <div>
          <span className="eyebrow">At a glance</span>
          <h2>Your projects are ready when you are.</h2>
          <p>{data.status.runningProjects} applications running · {data.status.updatesAvailable} update{data.status.updatesAvailable === 1 ? '' : 's'} available</p>
        </div>
        <div className="hero-panel__actions">
          <Button variant="primary" icon={Rocket} disabled={!outdated.length} onClick={deployAllUpdates}>
            {outdated.length ? `Deploy ${outdated.length} update${outdated.length === 1 ? '' : 's'}` : 'Everything is up to date'}
          </Button>
        </div>
      </section>

      <section aria-labelledby="system-heading">
        <div className="section-heading">
          <div><span className="eyebrow">Service status</span><h2 id="system-heading">System</h2></div>
          <Link className="text-link" to="/system">View diagnostics <ArrowRight aria-hidden="true" size={15} /></Link>
        </div>
        <div className="system-grid">
          <SystemComponent name="PC" status={data.status.pc} />
          <SystemComponent name="Abode to Web" status={data.status.abodeToWeb} />
          <SystemComponent name="Public gateway" status={data.status.gateway} />
          <SystemComponent name="Cloudflare Tunnel" status={data.status.cloudflareTunnel} />
        </div>
      </section>

      <section aria-labelledby="capacity-heading">
        <h2 id="capacity-heading" className="sr-only">Capacity</h2>
        <Capacity status={data.status} />
      </section>

      <section aria-labelledby="projects-heading">
        <div className="section-heading">
          <div><span className="eyebrow">Applications</span><h2 id="projects-heading">Projects</h2></div>
          <Link className="text-link" to="/projects">Manage projects <ArrowRight aria-hidden="true" size={15} /></Link>
        </div>
        <div className="project-grid">
          {data.projects.map((project) => <ProjectCard project={project} key={project.id} onChanged={reload} />)}
          {data.externalSites.map((site) => <ExternalSiteCard site={site} key={site.id} />)}
          {data.localProjects.map((project) => <LocalProjectCard project={project} key={project.id} />)}
        </div>
      </section>

      <details className="panel log-panel" open={failedDeployments.length > 0 || undefined}>
        <summary>
          <Rocket aria-hidden="true" size={17} />
          <div><span className="eyebrow">Logs</span><h2>Deployments &amp; activity</h2></div>
          {failedDeployments.length
            ? <span className="log-panel__badge">{failedDeployments.length} failed</span>
            : <span className="log-panel__ok"><ShieldCheck aria-hidden="true" size={15} />All healthy</span>}
          <ChevronDown className="log-panel__caret" aria-hidden="true" size={17} />
        </summary>
        <div className="log-panel__body">
          <DeploymentList deployments={data.recentDeployments.slice(0, 5)} projects={data.projects.map(({ id, name }) => ({ id, name }))} />
          <div className="log-panel__links">
            <Link className="text-link" to="/deployments">All deployments <ArrowRight aria-hidden="true" size={15} /></Link>
            <Link className="text-link" to="/activity">Activity log <ArrowRight aria-hidden="true" size={15} /></Link>
          </div>
        </div>
      </details>
    </div>
  )
}

function SystemComponent({ name, status }: { name: string; status: ComponentStatus }) {
  return (
    <article className="system-card">
      <span className="system-card__icon" aria-hidden="true"><Boxes size={18} /></span>
      <div><span>{name}</span><strong>{status.label}</strong><small>{status.detail ?? 'No additional detail'}</small></div>
      <StatusPill value={status.condition} label={status.label} />
    </article>
  )
}

function Capacity({ status }: { status: SystemStatus }) {
  const usedDisk = status.diskTotalBytes - status.diskFreeBytes
  const items = [
    { label: 'CPU', value: status.cpuPercent, detail: formatPercent(status.cpuPercent), icon: Cpu },
    { label: 'Memory', value: status.memoryUsedBytes / status.memoryTotalBytes * 100, detail: `${formatBytes(status.memoryUsedBytes)} / ${formatBytes(status.memoryTotalBytes)}`, icon: MemoryStick },
    { label: 'Disk', value: usedDisk / status.diskTotalBytes * 100, detail: `${formatBytes(status.diskFreeBytes)} free`, icon: HardDrive },
  ]
  return <div className="capacity-grid">{items.map(({ label, value, detail, icon: Icon }) => <article className="capacity-card" key={label}>
    <div className="capacity-card__head"><span><Icon aria-hidden="true" size={17} />{label}</span><strong>{detail}</strong></div>
    <div className="meter" role="meter" aria-label={`${label} usage`} aria-valuenow={Math.round(value)} aria-valuemin={0} aria-valuemax={100}><span style={{ width: `${Math.min(value, 100)}%` }} /></div>
  </article>)}</div>
}

function DeploymentList({ deployments, projects }: { deployments: DeploymentSummary[]; projects: { id: string; name: string }[] }) {
  return <div className="event-list">{deployments.map((deployment) => <Link className="event-row" to={`/projects/${deployment.projectId}/deployments`} key={deployment.id}>
    <span className={`event-row__mark event-row__mark--${deployment.status}`} aria-hidden="true" />
    <span className="event-row__main"><strong>{projects.find((project) => project.id === deployment.projectId)?.name ?? deployment.projectId}</strong><small>{deployment.safeFailureMessage ?? deployment.commitMessage ?? 'Deployment'}</small></span>
    <span className="event-row__meta"><code>{deployment.shortSha}</code><small>{formatRelativeTime(deployment.completedAt ?? deployment.createdAt)}</small></span>
  </Link>)}</div>
}
