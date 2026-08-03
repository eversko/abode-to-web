import { FolderGit2, Globe2, Plus, Search } from 'lucide-react'
import { useCallback, useMemo, useState } from 'react'
import { Link } from 'react-router'
import { useApi } from '../api/context'
import { EmptyState, ErrorState, LoadingState } from '../components/ResourceState'
import { useResource } from '../hooks/useResource'
import { ExternalSiteCard, ExternalSiteForm } from '../projects/ExternalSiteCard'
import { LocalProjectCard } from '../projects/LocalProjectCard'
import { ProjectCard } from '../projects/ProjectCard'

export function ProjectsPage() {
  const api = useApi()
  const load = useCallback((signal: AbortSignal) =>
    Promise.all([api.listProjects(signal), api.listExternalSites(signal), api.listLocalProjects(signal)])
      .then(([projects, externalSites, localProjects]) => ({ projects, externalSites, localProjects })), [api])
  const { data, error, loading, reload } = useResource(load)
  const [query, setQuery] = useState('')
  const [addingExternal, setAddingExternal] = useState(false)
  const lowered = query.toLowerCase()
  const projects = useMemo(() => data?.projects.filter((project) =>
    `${project.name} ${project.description ?? ''} ${project.hostname ?? ''}`.toLowerCase().includes(lowered)) ?? [], [data, lowered])
  const sites = useMemo(() => data?.externalSites.filter((site) =>
    `${site.name} ${site.description ?? ''} ${site.provider} ${site.url}`.toLowerCase().includes(lowered)) ?? [], [data, lowered])
  const locals = useMemo(() => data?.localProjects.filter((project) =>
    `${project.name} ${project.description ?? ''} ${project.repositoryPath} ${project.plannedHostname ?? ''}`.toLowerCase().includes(lowered)) ?? [], [data, lowered])
  const total = projects.length + sites.length + locals.length

  return (
    <div className="page-stack">
      <section className="page-intro">
        <div><span className="eyebrow">Applications</span><h2>Your projects</h2><p>Everything you host in one place — Abode to Web applications and externally hosted sites.</p></div>
        <div className="page-intro__actions">
          <button type="button" className="button button--quiet" onClick={() => setAddingExternal((value) => !value)}><Globe2 aria-hidden="true" size={16} /><span>{addingExternal ? 'Cancel external site' : 'Add external site'}</span></button>
          <Link className="button button--primary" to="/projects/new"><Plus aria-hidden="true" size={16} /><span>Add project</span></Link>
        </div>
      </section>
      {addingExternal && <ExternalSiteForm onSaved={() => { setAddingExternal(false); reload() }} />}
      <div className="toolbar">
        <label className="search-field"><Search aria-hidden="true" size={17} /><span className="sr-only">Search projects and sites</span><input value={query} onChange={(event) => setQuery(event.target.value)} placeholder="Search projects and sites" type="search" /></label>
        <span>{projects.length} project{projects.length === 1 ? '' : 's'}{sites.length ? ` · ${sites.length} external site${sites.length === 1 ? '' : 's'}` : ''}{locals.length ? ` · ${locals.length} local` : ''}</span>
      </div>
      {loading && !data ? <LoadingState label="Loading projects" /> : error && !data ? <ErrorState error={error} retry={reload} /> : total
        ? <div className="project-grid project-grid--wide">
          {projects.map((project) => <ProjectCard key={project.id} project={project} onChanged={reload} />)}
          {sites.map((site) => <ExternalSiteCard key={site.id} site={site} />)}
          {locals.map((project) => <LocalProjectCard key={project.id} project={project} />)}
        </div>
        : <EmptyState icon={<FolderGit2 />} title={query ? 'No matching projects or sites' : 'Add your first project'} action={!query && <Link className="button button--primary" to="/projects/new"><Plus aria-hidden="true" size={16} />Add project</Link>}>
          {query ? 'Try a different name, hostname, provider, or description.' : 'Choose a repository inside an allowed project root. Abode to Web will inspect it and suggest a safe configuration.'}
        </EmptyState>}
    </div>
  )
}
