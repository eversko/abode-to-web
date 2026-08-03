import { Check, X } from 'lucide-react'
import type { DeploymentStatus, DeploymentSummary } from '../api/schema'

type StepState = 'done' | 'active' | 'failed' | 'pending'

const STAGES = [
  { key: 'fetch', label: 'Fetch' },
  { key: 'build', label: 'Build' },
  { key: 'start', label: 'Start' },
  { key: 'health', label: 'Health' },
  { key: 'route', label: 'Route' },
  { key: 'live', label: 'Live' },
] as const

// Which stage index a live status is currently working on.
const statusStage = (status: DeploymentStatus): number => {
  switch (status) {
    case 'queued': return -1
    case 'fetching': case 'resolving-commit': case 'preparing-worktree': return 0
    case 'installing': case 'building': return 1
    case 'starting-candidate': return 2
    case 'verifying-candidate': return 3
    case 'switching-route': case 'draining-previous': return 4
    case 'succeeded': return 5
    default: return -1 // terminal failures handled separately
  }
}

// Build a 6-step model from a real deployment. Failures are located from the
// buildSucceeded / healthSucceeded flags Abode to Web already reports.
export function deployTimelineModel(deployment: Pick<DeploymentSummary, 'status' | 'buildSucceeded' | 'healthSucceeded'>): {
  label: string
  state: StepState
}[] {
  const { status } = deployment
  if (status === 'succeeded') return STAGES.map((s) => ({ label: s.label, state: 'done' }))

  const terminalFail = status === 'failed' || status === 'cancelled' || status === 'rolled-back'
  if (terminalFail) {
    const failedAt = !deployment.buildSucceeded ? 1 : !deployment.healthSucceeded ? 3 : 4
    return STAGES.map((s, i) => ({
      label: s.label,
      state: i < failedAt ? 'done' : i === failedAt ? 'failed' : 'pending',
    }))
  }

  const current = statusStage(status)
  return STAGES.map((s, i) => ({
    label: s.label,
    state: i < current ? 'done' : i === current ? 'active' : 'pending',
  }))
}

const srState: Record<StepState, string> = { done: 'complete', active: 'in progress', failed: 'failed', pending: 'pending' }

export function DeployTimeline({ deployment, variant = 'vertical' }: {
  deployment: Pick<DeploymentSummary, 'status' | 'buildSucceeded' | 'healthSucceeded'>
  variant?: 'vertical' | 'inline'
}) {
  const steps = deployTimelineModel(deployment)
  return (
    <ol className={`deploy-timeline deploy-timeline--${variant}`} aria-label="Deployment progress">
      {steps.map((step) => (
        <li key={step.label} className={`deploy-step deploy-step--${step.state}`}>
          <span className="deploy-step__node" aria-hidden="true">
            {step.state === 'done' && <Check size={11} strokeWidth={3} />}
            {step.state === 'failed' && <X size={11} strokeWidth={3} />}
          </span>
          <span className="deploy-step__label">
            {step.label}
            <span className="sr-only"> — {srState[step.state]}</span>
          </span>
        </li>
      ))}
    </ol>
  )
}
