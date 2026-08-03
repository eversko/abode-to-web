import type { AbodeToWebApiClient, LogStreamOptions, ProjectAction, StreamHandlers } from './client'
import type {
  ActivityEvent,
  CreateProjectRequest,
  DeploymentSummary,
  ExternalSiteSummary,
  LocalProjectSummary,
  LogEntry,
  OnboardingDetection,
  OnboardingSource,
  OnboardingValidation,
  OperationAccepted,
  OperationKind,
  OperationProgress,
  ProjectDetail,
  ProjectSummary,
  SystemOverview,
  TunnelStatus,
} from './schema'

const now = Date.now()
const isoAgo = (minutes: number) => new Date(now - minutes * 60_000).toISOString()

const deployments: DeploymentSummary[] = [
  {
    id: 'dep-studio-c', projectId: 'studio', commitSha: 'a73c81f88ca6', shortSha: 'a73c81f', branch: 'main',
    status: 'succeeded', commitMessage: 'Refine portfolio layout', commitAuthor: 'Developer', createdAt: isoAgo(42),
    completedAt: isoAgo(39), duration: '00:03:08', buildSucceeded: true, healthSucceeded: true, isActive: true, isKnownGood: true, safeFailureMessage: null,
  },
  {
    id: 'dep-api-demo', projectId: 'api-demo', commitSha: '8b10e2d119f0', shortSha: '8b10e2d', branch: 'release',
    status: 'succeeded', commitMessage: 'Improve request handling', commitAuthor: 'Developer', createdAt: isoAgo(235),
    completedAt: isoAgo(231), duration: '00:04:11', buildSucceeded: true, healthSucceeded: true, isActive: true, isKnownGood: true, safeFailureMessage: null,
  },
  {
    id: 'dep-docs-failed', projectId: 'docs', commitSha: 'fe2a883928aa', shortSha: 'fe2a883', branch: 'main',
    status: 'failed', commitMessage: 'Update documentation renderer', commitAuthor: 'Developer', createdAt: isoAgo(1_530),
    completedAt: isoAgo(1_527), duration: '00:02:44', buildSucceeded: false, healthSucceeded: false, isActive: false,
    isKnownGood: false, safeFailureMessage: 'Build exited with code 1. The previous deployment was preserved.',
  },
  {
    id: 'dep-studio-a', projectId: 'studio', commitSha: '01f8d22cc8ca', shortSha: '01f8d22', branch: 'main',
    status: 'succeeded', commitMessage: 'Improve project navigation', commitAuthor: 'Developer', createdAt: isoAgo(2_900),
    completedAt: isoAgo(2_897), duration: '00:02:51', buildSucceeded: true, healthSucceeded: true, isActive: false, isKnownGood: true, safeFailureMessage: null,
  },
]

const projects: ProjectSummary[] = [
  {
    id: 'studio', name: 'Studio', description: 'Team portfolio site', runtimeState: 'running', health: 'healthy',
    git: { branch: 'main', deployedCommit: 'a73c81f', remoteCommit: 'a73c81f', updateAvailable: false, commitMessage: 'Refine portfolio layout', commitAuthor: 'Developer', commitDate: isoAgo(42), checkedAt: isoAgo(3), error: null },
    hostname: 'studio.example.com', visibility: 'public', protected: true, lastDeploymentAt: isoAgo(39), startedAt: isoAgo(38),
  },
  {
    id: 'docs', name: 'Docs', description: 'Product documentation', runtimeState: 'stopped', health: 'stopped',
    git: { branch: 'main', deployedCommit: '6f4c920', remoteCommit: 'fe2a883', updateAvailable: true, commitMessage: 'Update documentation renderer', commitAuthor: 'Developer', commitDate: isoAgo(1_530), checkedAt: isoAgo(8), error: null },
    hostname: 'docs.example.com', visibility: 'public', protected: false, lastDeploymentAt: isoAgo(1_527), startedAt: null,
  },
  {
    id: 'api-demo', name: 'API Demo', description: 'Sample API service', runtimeState: 'running', health: 'healthy',
    git: { branch: 'release', deployedCommit: '8b10e2d', remoteCommit: '8b10e2d', updateAvailable: false, commitMessage: 'Improve request handling', commitAuthor: 'Developer', commitDate: isoAgo(235), checkedAt: isoAgo(5), error: null },
    hostname: 'api.example.com', visibility: 'public', protected: false, lastDeploymentAt: isoAgo(231), startedAt: isoAgo(229),
  },
]

const externalSites: ExternalSiteSummary[] = [
  {
    id: 'status-page', name: 'Status Page', description: 'Public service status', url: 'https://status.example.com/',
    provider: 'Example Hosting', managementUrl: 'https://hosting.example.com/',
    repositoryPath: 'C:/Projects/status-page', health: 'healthy', httpStatusCode: 200,
    observedAt: isoAgo(2), safeStatusMessage: null,
  },
  {
    id: 'marketing-site', name: 'Marketing Site', description: 'Public marketing site', url: 'https://www.example.com/',
    provider: 'Example Hosting', managementUrl: 'https://hosting.example.com/',
    repositoryPath: 'C:/Projects/marketing-site', health: 'healthy', httpStatusCode: 200,
    observedAt: isoAgo(2), safeStatusMessage: null,
  },
]

const localProjects: LocalProjectSummary[] = [
  {
    id: 'desktop-client', name: 'Desktop Client', description: 'Sample desktop application', repositoryPath: 'C:/Projects/desktop-client',
    plannedHostname: null, notes: null, exists: true, branch: 'main', lastCommitSha: '4be91c02aa10',
    lastCommitShortSha: '4be91c0', lastCommitMessage: 'feat: improve settings', lastCommitAuthor: 'Developer',
    lastCommitDate: isoAgo(600), observedAt: isoAgo(1), safeStatusMessage: null,
  },
  {
    id: 'automation-tool', name: 'Automation Tool', description: 'Sample command-line utility', repositoryPath: 'C:/Projects/automation-tool',
    plannedHostname: null, notes: null, exists: true, branch: 'main', lastCommitSha: '77aa02c1e9b3',
    lastCommitShortSha: '77aa02c', lastCommitMessage: 'fix: retry loop', lastCommitAuthor: 'Developer',
    lastCommitDate: isoAgo(3_000), observedAt: isoAgo(1), safeStatusMessage: null,
  },
  {
    id: 'planned-site', name: 'Planned Site', description: 'Static export pending first deploy', repositoryPath: 'C:/Projects/planned-site',
    plannedHostname: 'planned.example.com', notes: 'Needs first deploy', exists: true, branch: 'main',
    lastCommitSha: '90d1f00b2c44', lastCommitShortSha: '90d1f00', lastCommitMessage: 'build: static export config',
    lastCommitAuthor: 'Developer', lastCommitDate: isoAgo(120), observedAt: isoAgo(1), safeStatusMessage: null,
  },
  {
    id: 'missing-project', name: 'Missing Project', description: 'Moved or deleted repository', repositoryPath: 'C:/Projects/missing-project',
    plannedHostname: null, notes: null, exists: false, branch: null, lastCommitSha: null, lastCommitShortSha: null,
    lastCommitMessage: null, lastCommitAuthor: null, lastCommitDate: null, observedAt: isoAgo(1),
    safeStatusMessage: 'The repository folder was not found on disk.',
  },
]

const activity: ActivityEvent[] = [
  { sequence: 4, timestamp: isoAgo(39), action: 'Deploy', outcome: 'succeeded', actor: 'Local dashboard', projectId: 'studio', summary: 'Studio a73c81f became active after passing health checks.' },
  { sequence: 3, timestamp: isoAgo(231), action: 'Deploy', outcome: 'succeeded', actor: 'Local dashboard', projectId: 'api-demo', summary: 'API Demo 8b10e2d became active.' },
  { sequence: 2, timestamp: isoAgo(1_527), action: 'Deploy', outcome: 'failed', actor: 'Local dashboard', projectId: 'docs', summary: 'Candidate build failed; the previous release remained available.' },
  { sequence: 1, timestamp: isoAgo(2_897), action: 'Deploy', outcome: 'succeeded', actor: 'Local dashboard', projectId: 'studio', summary: 'Studio 01f8d22 became active.' },
]

const tunnel: TunnelStatus = {
  state: 'connected', cloudflaredInstalled: true, version: '2026.6.1', serviceInstalled: true,
  serviceRunning: true, gatewayReachable: true, tunnelName: 'abode-to-web-home', checkedAt: isoAgo(1), safeDiagnostic: null,
}

const configuration = (project: ProjectSummary): ProjectDetail['desiredConfiguration'] => ({
  id: project.id,
  name: project.name,
  description: project.description,
  enabled: project.visibility !== 'disabled',
  autostart: false,
  protected: project.protected,
  demoGroups: ['showcase'],
  visibility: project.visibility,
  repository: { provider: 'github', localPath: `C:/Projects/${project.id}`, remote: 'origin', branch: project.git?.branch ?? 'main' },
  deployment: {
    adapter: 'node',
    mode: 'worktree',
    install: { executable: 'npm', arguments: ['ci'] },
    build: { executable: 'npm', arguments: ['run', 'build'] },
    start: { executable: 'npm', arguments: ['run', 'start'] },
    workingDirectory: '.',
    environmentFile: '.env.abode-to-web',
    staticOutputDirectory: null,
    portEnvironmentVariable: 'PORT',
    candidateStartupTimeoutSeconds: 120,
    drainSeconds: 5,
  },
  runtime: {
    internalPort: project.id === 'studio' ? 5101 : project.id === 'docs' ? 5102 : 5103,
    restartPolicy: 'on-failure', maxRestarts: 5, restartWindowSeconds: 300, initialBackoffSeconds: 1,
    maxBackoffSeconds: 60, stableRuntimeSeconds: 300, gracefulShutdownSeconds: 15,
  },
  health: { type: 'http', path: '/health', intervalSeconds: 20, timeoutSeconds: 5, healthyThreshold: 2, unhealthyThreshold: 3 },
  routing: {
    hostname: project.hostname,
    accessApplicationAudience: project.visibility === 'protected' ? 'example-access-application-audience' : null,
    offlinePage: true,
    requestTimeoutSeconds: 100,
    maxRequestBodyBytes: 25 * 1024 * 1024,
  },
})

const makeOperation = (kind: OperationKind): OperationAccepted => {
  const operationId = crypto.randomUUID()
  return { operationId, kind, progressUrl: `/mock/operations/${operationId}` }
}

const actionKind = (action: ProjectAction): OperationKind => {
  if (action === 'check-for-updates') return 'check-for-updates'
  if (action === 'force-stop') return 'stop'
  return action
}

export class MockAbodeToWebApiClient implements AbodeToWebApiClient {
  readonly capabilities = { changeHostname: true, changeVisibility: true, deleteProject: true } as const

  getOverview(): Promise<SystemOverview> {
    return Promise.resolve({
      status: {
        observedAt: new Date().toISOString(), pc: { condition: 'healthy', label: 'Online', detail: 'Windows 11 Pro' },
        abodeToWeb: { condition: 'healthy', label: 'Healthy', detail: 'Service uptime 3d 14h' },
        gateway: { condition: 'healthy', label: 'Healthy', detail: '127.0.0.1:4580' },
        cloudflareTunnel: { condition: 'healthy', label: 'Connected', detail: 'abode-to-web-home' },
        runningProjects: 2, unhealthyProjects: 0, updatesAvailable: 1, cpuPercent: 18,
        memoryUsedBytes: 10_522_984_448, memoryTotalBytes: 34_359_738_368, diskFreeBytes: 441_307_250_688,
        diskTotalBytes: 1_000_202_043_392, quietMode: false,
      },
      projects: [...projects], recentDeployments: [...deployments], recentActivity: [...activity],
    })
  }

  listProjects() { return Promise.resolve([...projects]) }
  listExternalSites() { return Promise.resolve([...externalSites]) }
  saveExternalSite() { return Promise.resolve() }
  listLocalProjects() { return Promise.resolve([...localProjects]) }
  saveLocalProject() { return Promise.resolve() }

  getProject(projectId: string): Promise<ProjectDetail> {
    const project = projects.find((item) => item.id === projectId) ?? projects[0]
    return Promise.resolve({
      summary: { ...project }, desiredConfiguration: configuration(project),
      activeDeployment: deployments.find((deployment) => deployment.projectId === project.id && deployment.isActive),
      recentDeployments: deployments.filter((deployment) => deployment.projectId === project.id),
      secretKeys: [{ key: 'DATABASE_URL', hasValue: true, updatedAt: isoAgo(4_300) }, { key: 'SESSION_SECRET', hasValue: true, updatedAt: isoAgo(4_300) }],
    })
  }

  listDeployments(projectId?: string) { return Promise.resolve(deployments.filter((item) => !projectId || item.projectId === projectId)) }
  listActivity() { return Promise.resolve([...activity]) }
  getTunnelStatus() { return Promise.resolve({ ...tunnel }) }
  runProjectAction(_projectId: string, action: ProjectAction) { return Promise.resolve(makeOperation(actionKind(action))) }
  rollback() { return Promise.resolve(makeOperation('rollback')) }
  setQuietMode() { return Promise.resolve(makeOperation('quiet-mode')) }
  startShowcase() { return Promise.resolve(makeOperation('start-showcase')) }

  watchOperation(operation: OperationAccepted, handlers: StreamHandlers<OperationProgress>) {
    const stages = operation.kind === 'deploy'
      ? [['Preparing worktree', 12], ['Installing dependencies', 35], ['Building candidate', 62], ['Verifying health', 86], ['Switching route', 96], ['Complete', 100]] as const
      : [['Queued', 10], ['Applying change', 65], ['Complete', 100]] as const
    let index = 0
    handlers.onOpen?.()
    const timer = window.setInterval(() => {
      const stage = stages[index]
      const completed = index === stages.length - 1
      handlers.onMessage({
        operationId: operation.operationId, kind: operation.kind, status: completed ? 'succeeded' : 'running',
        stage: stage[0], percent: stage[1], message: completed ? 'Operation completed successfully.' : stage[0], timestamp: new Date().toISOString(),
      })
      index += 1
      if (completed) window.clearInterval(timer)
    }, 520)
    return () => window.clearInterval(timer)
  }

  streamLogs(projectId: string | undefined, options: LogStreamOptions, handlers: StreamHandlers<LogEntry>) {
    const messages = [
      ['information', 'Application listening on 127.0.0.1:5101'],
      ['information', 'GET /health 200 4ms'],
      ['warning', 'Response time exceeded 250ms for GET /api/projects'],
      ['information', 'Health probe passed'],
    ] as const
    let sequence = 1
    handlers.onOpen?.()
    const emit = () => {
      const message = messages[(sequence - 1) % messages.length]
      handlers.onMessage({ sequence, timestamp: new Date().toISOString(), source: options.sources?.[0] ?? 'runtime', severity: message[0], message: message[1], projectId: projectId ?? null })
      sequence += 1
    }
    emit()
    const timer = window.setInterval(emit, 2_800)
    return () => window.clearInterval(timer)
  }

  getLogDownloadUrl(projectId: string | undefined) { return projectId ? `/mock/projects/${encodeURIComponent(projectId)}/logs?download=true&limit=2000` : '/mock/system/logs?download=true&limit=2000' }

  detectProject(source: OnboardingSource): Promise<OnboardingDetection> {
    const repositoryPath = source.kind === 'existing' ? source.path : `C:/Projects/${source.destinationName ?? 'new-project'}`
    return Promise.resolve({
      detectionId: crypto.randomUUID(), expiresAt: new Date(Date.now() + 15 * 60_000).toISOString(), repositoryPath,
      isGitRepository: true, framework: 'vite', packageManager: 'npm', lockFile: 'package-lock.json',
      suggestedRepositoryProvider: 'github', gitRemotes: ['origin'], suggestedRemote: 'origin', suggestedBranch: 'main',
      suggestedCommands: [
        { id: 'npm-ci', purpose: 'install', executable: 'npm', arguments: ['ci'], reason: 'package-lock.json was found.' },
        { id: 'npm-build', purpose: 'build', executable: 'npm', arguments: ['run', 'build'], reason: 'The package defines a build script.' },
        { id: 'npm-start', purpose: 'start', executable: 'npm', arguments: ['run', 'start'], reason: 'The package defines a start script.' },
      ],
      suggestedHealthPaths: ['/health', '/'], suggestedStaticOutputDirectory: 'dist',
      suggestedPortEnvironmentVariable: 'PORT', suggestedPort: 5104, warnings: ['Review every suggested command before saving.'],
    })
  }

  cloneProject(repository: string) {
    const name = repository.split('/').at(-1) ?? 'new-project'
    return Promise.resolve({ ...makeOperation('test-build'), repositoryPath: `C:/Projects/${name}` })
  }

  validateProject(request: CreateProjectRequest): Promise<OnboardingValidation> {
    const issues = []
    if (!/^[a-z0-9-]+$/.test(request.id)) issues.push({ code: 'invalid_id', path: 'id', message: 'Use lowercase letters, numbers, and hyphens.', isWarning: false })
    if (!request.hostname.includes('.')) issues.push({ code: 'invalid_hostname', path: 'hostname', message: 'Enter a complete hostname.', isWarning: false })
    if (request.visibility === 'protected' && !request.accessApplicationAudience?.trim()) issues.push({ code: 'access_audience_required', path: 'accessApplicationAudience', message: 'Enter the Cloudflare Access application audience.', isWarning: false })
    return Promise.resolve({ isValid: issues.length === 0, issues })
  }

  createProject() { return Promise.resolve(makeOperation('test-build')) }
  changeHostname() { return Promise.resolve(makeOperation('change-hostname')) }
  changeVisibility() { return Promise.resolve(makeOperation('change-visibility')) }
  getEnvironmentExampleUrl(projectId: string) { return `/api/v1/projects/${encodeURIComponent(projectId)}/secrets/example` }
  replaceSecret() { return Promise.resolve() }
  deleteSecret() { return Promise.resolve() }
  deleteProject() { return Promise.resolve(makeOperation('remove-project')) }
}
