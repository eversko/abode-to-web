import { apiRoutes } from './routes'
import type {
  ActivityEvent,
  AppVisibility,
  ChangeHostnameRequest,
  CloneProjectAccepted,
  CreateProjectRequest,
  DeploymentSummary,
  ExternalSiteSummary,
  LocalProjectSummary,
  LogEntry,
  LogSeverity,
  LogSource,
  OnboardingDetection,
  OnboardingSource,
  OnboardingValidation,
  OperationAccepted,
  OperationProgress,
  ProjectDetail,
  ProjectSummary,
  SaveExternalSiteRequest,
  SaveLocalProjectRequest,
  QuietModeRequest,
  ShowcaseRequest,
  SystemOverview,
  SystemStatusWire,
  TunnelStatus,
} from './schema'

export type ProjectAction = 'start' | 'stop' | 'restart' | 'deploy' | 'check-for-updates' | 'force-stop'

export type LogStreamOptions = {
  sources?: LogSource[]
  minimumSeverity?: LogSeverity
  search?: string
}

export type StreamHandlers<T> = {
  onMessage: (message: T) => void
  onError: (error: Error) => void
  onOpen?: () => void
}

export type AbodeToWebApiClient = {
  capabilities: {
    changeHostname: boolean
    changeVisibility: boolean
    deleteProject: boolean
  }
  getOverview: (signal?: AbortSignal) => Promise<SystemOverview>
  listProjects: (signal?: AbortSignal) => Promise<ProjectSummary[]>
  listExternalSites: (signal?: AbortSignal) => Promise<ExternalSiteSummary[]>
  saveExternalSite: (siteId: string, request: SaveExternalSiteRequest) => Promise<void>
  listLocalProjects: (signal?: AbortSignal) => Promise<LocalProjectSummary[]>
  saveLocalProject: (projectId: string, request: SaveLocalProjectRequest) => Promise<void>
  getProject: (projectId: string, signal?: AbortSignal) => Promise<ProjectDetail>
  listDeployments: (projectId?: string, signal?: AbortSignal) => Promise<DeploymentSummary[]>
  listActivity: (signal?: AbortSignal) => Promise<ActivityEvent[]>
  getTunnelStatus: (signal?: AbortSignal) => Promise<TunnelStatus>
  runProjectAction: (projectId: string, action: ProjectAction) => Promise<OperationAccepted>
  rollback: (projectId: string, deploymentId: string) => Promise<OperationAccepted>
  setQuietMode: (enabled: boolean) => Promise<OperationAccepted>
  startShowcase: (projectIds?: string[]) => Promise<OperationAccepted>
  watchOperation: (operation: OperationAccepted, handlers: StreamHandlers<OperationProgress>) => () => void
  streamLogs: (projectId: string | undefined, options: LogStreamOptions, handlers: StreamHandlers<LogEntry>) => () => void
  getLogDownloadUrl: (projectId: string | undefined, options: LogStreamOptions) => string
  detectProject: (source: OnboardingSource) => Promise<OnboardingDetection>
  cloneProject: (repository: string) => Promise<CloneProjectAccepted>
  validateProject: (request: CreateProjectRequest) => Promise<OnboardingValidation>
  createProject: (request: CreateProjectRequest) => Promise<OperationAccepted>
  changeHostname: (projectId: string, hostname: string) => Promise<OperationAccepted>
  changeVisibility: (projectId: string, visibility: AppVisibility) => Promise<OperationAccepted>
  getEnvironmentExampleUrl: (projectId: string) => string
  replaceSecret: (projectId: string, key: string, value: string) => Promise<void>
  deleteSecret: (projectId: string, key: string) => Promise<void>
  deleteProject: (projectId: string, removeRepository: boolean) => Promise<OperationAccepted>
}

export class ApiError extends Error {
  readonly status: number
  readonly code?: string

  constructor(message: string, status = 0, code?: string) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.code = code
  }
}

type ProblemDetails = {
  title?: string
  detail?: string
  code?: string
  errors?: Record<string, string[]>
}

type CsrfResponse = { token: string }

const buildSearch = (options: LogStreamOptions) => {
  const search = new URLSearchParams()
  if (options.sources?.[0]) search.set('source', options.sources[0])
  if (options.minimumSeverity) search.set('severity', options.minimumSeverity)
  if (options.search) search.set('search', options.search)
  return search
}

export class HttpAbodeToWebApiClient implements AbodeToWebApiClient {
  readonly capabilities = { changeHostname: true, changeVisibility: true, deleteProject: true } as const
  private csrfToken?: string

  private async request<T>(path: string, init: RequestInit = {}, mutation = false): Promise<T> {
    const headers = new Headers(init.headers)
    headers.set('Accept', 'application/json')
    if (init.body) headers.set('Content-Type', 'application/json')
    if (mutation) headers.set('X-CSRF-TOKEN', await this.getCsrfToken())

    let response: Response
    try {
      response = await fetch(path, { ...init, headers, credentials: 'same-origin' })
    } catch {
      throw new ApiError('Abode to Web is unreachable. Check that the Windows service is running.')
    }

    if (!response.ok) {
      const problem = await response.json().catch(() => null) as ProblemDetails | null
      const fieldError = problem?.errors ? Object.values(problem.errors).flat()[0] : undefined
      throw new ApiError(problem?.detail ?? fieldError ?? problem?.title ?? 'Abode to Web could not complete the request.', response.status, problem?.code)
    }

    if (response.status === 204) return undefined as T
    return await response.json() as T
  }

  private async getCsrfToken(): Promise<string> {
    if (!this.csrfToken) {
      const result = await this.request<CsrfResponse>(apiRoutes.csrf)
      this.csrfToken = result.token
    }
    return this.csrfToken
  }

  getOverview(signal?: AbortSignal) {
    return Promise.all([
      this.request<SystemStatusWire>(apiRoutes.systemStatus, { signal }),
      this.request<ProjectSummary[]>(apiRoutes.projects, { signal }),
      this.request<DeploymentSummary[]>(apiRoutes.deployments, { signal }),
      this.request<ActivityEvent[]>(apiRoutes.activity, { signal }),
    ]).then(([status, projects, recentDeployments, recentActivity]) => ({
      status: {
        ...status,
        runningProjects: Number(status.runningProjects),
        unhealthyProjects: Number(status.unhealthyProjects),
        updatesAvailable: Number(status.updatesAvailable),
        cpuPercent: Number(status.cpuPercent),
        memoryUsedBytes: Number(status.memoryUsedBytes),
        memoryTotalBytes: Number(status.memoryTotalBytes),
        diskFreeBytes: Number(status.diskFreeBytes),
        diskTotalBytes: Number(status.diskTotalBytes),
      },
      projects,
      recentDeployments,
      recentActivity,
    }))
  }

  listProjects(signal?: AbortSignal) {
    return this.request<ProjectSummary[]>(apiRoutes.projects, { signal })
  }

  listExternalSites(signal?: AbortSignal) {
    return this.request<ExternalSiteSummary[]>(apiRoutes.externalSites, { signal })
  }

  saveExternalSite(siteId: string, request: SaveExternalSiteRequest) {
    return this.request<undefined>(apiRoutes.externalSite(siteId), {
      method: 'PUT',
      body: JSON.stringify(request),
    }, true)
  }

  listLocalProjects(signal?: AbortSignal) {
    return this.request<LocalProjectSummary[]>(apiRoutes.localProjects, { signal })
  }

  saveLocalProject(projectId: string, request: SaveLocalProjectRequest) {
    return this.request<undefined>(apiRoutes.localProject(projectId), {
      method: 'PUT',
      body: JSON.stringify(request),
    }, true)
  }

  getProject(projectId: string, signal?: AbortSignal) {
    return this.request<ProjectDetail>(apiRoutes.project(projectId), { signal })
  }

  listDeployments(projectId?: string, signal?: AbortSignal) {
    const path = projectId ? apiRoutes.projectDeployments(projectId) : apiRoutes.deployments
    return this.request<DeploymentSummary[]>(path, { signal })
  }

  listActivity(signal?: AbortSignal) {
    return this.request<ActivityEvent[]>(apiRoutes.activity, { signal })
  }

  getTunnelStatus(signal?: AbortSignal) {
    return this.request<TunnelStatus>(apiRoutes.tunnelStatus, { signal })
  }

  runProjectAction(projectId: string, action: ProjectAction) {
    return this.request<OperationAccepted>(apiRoutes.projectAction(projectId, action), { method: 'POST', body: '{}' }, true)
  }

  rollback(projectId: string, deploymentId: string) {
    return this.request<OperationAccepted>(apiRoutes.rollback(projectId), {
      method: 'POST',
      body: JSON.stringify({ deploymentId }),
    }, true)
  }

  setQuietMode(enabled: boolean) {
    const body: QuietModeRequest = { enabled }
    return this.request<OperationAccepted>(apiRoutes.quietMode, { method: 'POST', body: JSON.stringify(body) }, true)
  }

  startShowcase(projectIds?: string[]) {
    const body: ShowcaseRequest = { projectIds: projectIds ?? null }
    return this.request<OperationAccepted>(apiRoutes.showcase, { method: 'POST', body: JSON.stringify(body) }, true)
  }

  watchOperation(operation: OperationAccepted, handlers: StreamHandlers<OperationProgress>) {
    const source = new EventSource(apiRoutes.operationEvents, { withCredentials: true })
    let consecutivePollFailures = 0
    let disposed = false
    const deliver = (progress: OperationProgress) => {
      if (!disposed && progress.operationId === operation.operationId) handlers.onMessage(progress)
    }
    const poll = async () => {
      try {
        deliver(await this.request<OperationProgress>(apiRoutes.operation(operation.operationId)))
        consecutivePollFailures = 0
      } catch (reason) {
        consecutivePollFailures += 1
        if (consecutivePollFailures >= 3) handlers.onError(reason instanceof Error ? reason : new Error('Operation progress is unavailable.'))
      }
    }
    source.onopen = () => handlers.onOpen?.()
    const receive = (event: MessageEvent<string>) => {
      try {
        deliver(JSON.parse(event.data) as OperationProgress)
      } catch {
        handlers.onError(new Error('Abode to Web sent an invalid operation update.'))
      }
    }
    source.addEventListener('operation', receive)
    const timer = window.setInterval(() => { void poll() }, 1_200)
    void poll()
    return () => {
      disposed = true
      window.clearInterval(timer)
      source.close()
    }
  }

  streamLogs(projectId: string | undefined, options: LogStreamOptions, handlers: StreamHandlers<LogEntry>) {
    const query = buildSearch(options)
    if (projectId) query.set('projectId', projectId)
    const path = `${apiRoutes.logEvents}?${query.toString()}`
    const source = new EventSource(path, { withCredentials: true })
    const historyPath = `${apiRoutes.logs}?${query.toString()}`
    void this.request<LogEntry[]>(historyPath).then((entries) => entries.forEach(handlers.onMessage)).catch(() => undefined)
    source.onopen = () => handlers.onOpen?.()
    const receive = (event: MessageEvent<string>) => {
      try {
        const entry = JSON.parse(event.data) as LogEntry
        const severityOrder: LogSeverity[] = ['trace', 'debug', 'information', 'warning', 'error', 'critical']
        const minimum = options.minimumSeverity ? severityOrder.indexOf(options.minimumSeverity) : 0
        const matchesSeverity = severityOrder.indexOf(entry.severity) >= minimum
        const matchesSearch = !options.search || entry.message.toLowerCase().includes(options.search.toLowerCase())
        if (matchesSeverity && matchesSearch) handlers.onMessage(entry)
      } catch {
        handlers.onError(new Error('Abode to Web sent an invalid log entry.'))
      }
    }
    source.addEventListener('log', receive)
    source.onerror = () => handlers.onError(new Error('Live log connection interrupted. Retrying…'))
    return () => source.close()
  }

  getLogDownloadUrl(projectId: string | undefined, options: LogStreamOptions) {
    const query = buildSearch(options)
    if (projectId) query.set('projectId', projectId)
    query.set('limit', '2000')
    return `${apiRoutes.logDownload}?${query.toString()}`
  }

  detectProject(source: OnboardingSource) {
    if (source.kind !== 'existing') return Promise.reject(new ApiError('Clone the GitHub repository before running detection.'))
    return this.request<OnboardingDetection>(apiRoutes.onboardingDetect, { method: 'POST', body: JSON.stringify({ localPath: source.path }) }, true)
  }

  cloneProject(repository: string) {
    return this.request<CloneProjectAccepted>(apiRoutes.onboardingClone, { method: 'POST', body: JSON.stringify({ repository }) }, true)
  }

  validateProject(request: CreateProjectRequest) {
    return this.request<OnboardingValidation>(apiRoutes.onboardingValidate, { method: 'POST', body: JSON.stringify(request) }, true)
  }

  createProject(request: CreateProjectRequest) {
    return this.request<OperationAccepted>(apiRoutes.onboardingProjects, { method: 'POST', body: JSON.stringify(request) }, true)
  }

  changeHostname(projectId: string, hostname: string) {
    const body: ChangeHostnameRequest = { hostname }
    return this.request<OperationAccepted>(apiRoutes.projectHostname(projectId), {
      method: 'PUT',
      body: JSON.stringify(body),
    }, true)
  }

  changeVisibility(projectId: string, visibility: AppVisibility) {
    return this.request<OperationAccepted>(apiRoutes.projectVisibility(projectId), {
      method: 'PUT',
      body: JSON.stringify({ visibility }),
    }, true)
  }

  getEnvironmentExampleUrl(projectId: string) {
    return apiRoutes.projectSecretExample(projectId)
  }

  replaceSecret(projectId: string, key: string, value: string) {
    return this.request<undefined>(apiRoutes.projectSecret(projectId, key), {
      method: 'PUT',
      body: JSON.stringify({ value }),
    }, true)
  }

  deleteSecret(projectId: string, key: string) {
    return this.request<undefined>(apiRoutes.projectSecret(projectId, key), { method: 'DELETE' }, true)
  }

  deleteProject(projectId: string, removeRepository: boolean) {
    return this.request<OperationAccepted>(apiRoutes.projectRemoval(projectId, removeRepository), { method: 'DELETE' }, true)
  }
}
