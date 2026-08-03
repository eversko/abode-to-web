/**
 * UI contract aliases.
 *
 * `generated.ts` is produced by openapi-typescript from the checked-in server
 * document. This file adds only the small UI-normalized and SSE-only shapes
 * that ASP.NET's current OpenAPI description cannot express yet.
 */
import type { components as OpenApiComponents } from './generated'

type ApiSchema = OpenApiComponents['schemas']

export type ComponentCondition = ApiSchema['ComponentCondition']
export type ComponentStatus = ApiSchema['ComponentStatus']

export type SystemStatusWire = ApiSchema['SystemStatus']
export type SystemStatus = Omit<SystemStatusWire,
  | 'runningProjects'
  | 'unhealthyProjects'
  | 'updatesAvailable'
  | 'cpuPercent'
  | 'memoryUsedBytes'
  | 'memoryTotalBytes'
  | 'diskFreeBytes'
  | 'diskTotalBytes'> & {
  runningProjects: number
  unhealthyProjects: number
  updatesAvailable: number
  cpuPercent: number
  memoryUsedBytes: number
  memoryTotalBytes: number
  diskFreeBytes: number
  diskTotalBytes: number
}

export type ProjectRuntimeState = ApiSchema['ProjectRuntimeState']
export type HealthState = ApiSchema['HealthState']
export type AppVisibility = ApiSchema['AppVisibility']
export type GitStatus = ApiSchema['GitStatus']
export type ProjectSummary = ApiSchema['ProjectSummary']
export type ExternalSiteSummary = ApiSchema['ExternalSiteSummary']
export type SaveExternalSiteRequest = ApiSchema['SaveExternalSiteRequest']
export type LocalProjectSummary = ApiSchema['LocalProjectSummary']
export type SaveLocalProjectRequest = ApiSchema['SaveLocalProjectRequest']
export type DeploymentStatus = ApiSchema['DeploymentStatus']
export type DeploymentSummary = ApiSchema['DeploymentSummary']

export type SecretKeyInfo = {
  key: string
  hasValue: boolean
  updatedAt?: string | null
}

export type CommandSpec = {
  executable: string
  arguments: string[]
  timeoutSeconds?: number
}

export type ProjectConfiguration = {
  id: string
  name: string
  description: string | null
  enabled: boolean
  autostart: boolean
  protected: boolean
  demoGroups: string[]
  visibility: AppVisibility
  repository: {
    provider: 'github' | 'other'
    localPath: string
    remote: string
    branch: string
  } | null
  deployment: {
    adapter: 'command' | 'node' | 'static' | 'docker-compose'
    mode: 'worktree' | 'local'
    install: CommandSpec | null
    build: CommandSpec | null
    start: CommandSpec | null
    workingDirectory: string
    environmentFile: string | null
    staticOutputDirectory: string | null
    portEnvironmentVariable: string
    candidateStartupTimeoutSeconds: number
    drainSeconds: number
  }
  runtime: {
    internalPort: number | null
    restartPolicy: 'never' | 'on-failure' | 'always'
    maxRestarts: number
    restartWindowSeconds: number
    initialBackoffSeconds: number
    maxBackoffSeconds: number
    stableRuntimeSeconds: number
    gracefulShutdownSeconds: number
  }
  health: {
    type: 'http' | 'tcp' | 'process' | 'none'
    path: string
    intervalSeconds: number
    timeoutSeconds: number
    healthyThreshold: number
    unhealthyThreshold: number
  }
  routing: {
    hostname: string | null
    accessApplicationAudience: string | null
    offlinePage: boolean
    requestTimeoutSeconds: number
    maxRequestBodyBytes: number
  }
}

export type ProjectDetail = {
  summary: ProjectSummary
  desiredConfiguration: ProjectConfiguration
  activeDeployment?: DeploymentSummary | null
  recentDeployments: DeploymentSummary[]
  secretKeys: SecretKeyInfo[]
  safeStatusMessage?: string | null
}

export type OperationKind =
  | 'start'
  | 'stop'
  | 'restart'
  | 'deploy'
  | 'rollback'
  | 'check-for-updates'
  | 'validate-configuration'
  | 'test-build'
  | 'quiet-mode'
  | 'start-showcase'
  | 'change-hostname'
  | 'remove-project'
  | 'change-visibility'

export type OperationStatus = 'queued' | 'running' | 'succeeded' | 'failed' | 'cancelled'

export type OperationAccepted = {
  operationId: string
  kind: OperationKind
  progressUrl: string
}

export type CloneProjectAccepted = ApiSchema['CloneProjectAccepted']

export type OperationProgress = {
  operationId: string
  kind: OperationKind
  status: OperationStatus
  stage: string
  percent?: number | null
  message: string
  timestamp: string
  errorCode?: string | null
}

export type LogSource = 'system' | 'runtime' | 'build' | 'audit' | 'cloudflare'
export type LogSeverity = 'trace' | 'debug' | 'information' | 'warning' | 'error' | 'critical'

export type LogEntry = {
  sequence: number
  timestamp: string
  source: LogSource
  severity: LogSeverity
  message: string
  projectId?: string | null
  deploymentId?: string | null
  operationId?: string | null
}

export type ActivityOutcome = 'started' | 'succeeded' | 'failed' | 'rejected'

export type ActivityEvent = {
  sequence: number
  timestamp: string
  action: string
  outcome: ActivityOutcome
  actor: string
  projectId?: string | null
  operationId?: string | null
  summary: string
}

export type TunnelConnectionState = ApiSchema['TunnelConnectionState']
export type TunnelStatus = ApiSchema['TunnelStatus']

export type ContractValidationIssue = ApiSchema['ContractValidationIssue']

export type DetectedFramework = ApiSchema['DetectedFramework']

export type CommandSuggestion = {
  id: string
  purpose: 'install' | 'build' | 'start'
  executable: string
  arguments: string[]
  reason: string
}

type OnboardingDetectionWire = ApiSchema['OnboardingDetection']
export type OnboardingDetection = Omit<OnboardingDetectionWire, 'suggestedCommands' | 'suggestedPort'> & {
  suggestedCommands: CommandSuggestion[]
  suggestedPort: number
}

export type OnboardingSource =
  | { kind: 'existing'; path: string }
  | { kind: 'github'; repositoryUrl: string; destinationName?: string }

type OnboardingValidationWire = ApiSchema['OnboardingValidation']
export type OnboardingValidation = Pick<OnboardingValidationWire, 'isValid' | 'issues'>
  & Partial<Pick<OnboardingValidationWire, 'normalizedManifest'>>

export type CreateProjectRequest = ApiSchema['CreateProjectRequest']
export type ChangeHostnameRequest = ApiSchema['ChangeHostnameRequest']
export type QuietModeRequest = ApiSchema['QuietModeRequest']
export type ShowcaseRequest = ApiSchema['ShowcaseRequest']

export type SystemOverview = {
  status: SystemStatus
  projects: ProjectSummary[]
  recentDeployments: DeploymentSummary[]
  recentActivity: ActivityEvent[]
}

export type DashboardSettings = {
  quietMode: boolean
  showcaseProjectIds: string[]
  projectRoot: string
  dashboardAddress: string
  gatewayAddress: string
}
