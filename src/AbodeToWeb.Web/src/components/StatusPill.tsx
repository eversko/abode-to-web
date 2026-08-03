import type { ComponentCondition, DeploymentStatus, HealthState, ProjectRuntimeState } from '../api/schema'
import { sentenceCase } from '../lib/format'

type StatusValue = ComponentCondition | HealthState | ProjectRuntimeState | DeploymentStatus | 'connected' | 'succeeded'

const statusTone = (value: StatusValue) => {
  if (['healthy', 'running', 'connected', 'succeeded', 'active'].includes(value)) return 'positive'
  if (['unhealthy', 'failed', 'circuit-open'].includes(value)) return 'negative'
  if (['degraded', 'recovering', 'starting', 'stopping', 'queued', 'fetching', 'resolving-commit', 'preparing-worktree', 'installing', 'building', 'starting-candidate', 'verifying-candidate', 'switching-route', 'draining-previous'].includes(value)) return 'warning'
  return 'neutral'
}

export function StatusPill({ value, label, dot = true }: { value: StatusValue; label?: string; dot?: boolean }) {
  return (
    <span className={`status-pill status-pill--${statusTone(value)}`}>
      {dot && <span className="status-pill__dot" aria-hidden="true" />}
      {label ?? sentenceCase(value)}
    </span>
  )
}
