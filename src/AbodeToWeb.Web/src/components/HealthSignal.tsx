import { AlertTriangle, CircleCheck, CircleHelp, CircleMinus, CirclePause, Loader2 } from 'lucide-react'
import type { LucideIcon } from 'lucide-react'
import type { HealthState, ProjectRuntimeState } from '../api/schema'
import { sentenceCase } from '../lib/format'

type Tone = 'positive' | 'warning' | 'negative' | 'neutral'

// One glanceable signal derived from the two machine states (runtimeState +
// health), replacing the double status pill. Colour is always paired with an
// icon + a short word, and the full machine state is exposed via title/aria-label.
export function healthSignal(runtimeState: ProjectRuntimeState, health: HealthState): {
  tone: Tone
  word: string
  Icon: LucideIcon
  spin: boolean
  full: string
} {
  const full = `${sentenceCase(runtimeState)} · ${sentenceCase(health)}`
  if (runtimeState === 'failed' || runtimeState === 'circuit-open' || health === 'unhealthy') {
    return { tone: 'negative', word: 'Down', Icon: AlertTriangle, spin: false, full }
  }
  if (health === 'degraded' || health === 'recovering') {
    return { tone: 'warning', word: health === 'recovering' ? 'Recovering' : 'Degraded', Icon: AlertTriangle, spin: false, full }
  }
  if (runtimeState === 'starting' || runtimeState === 'stopping' || health === 'starting') {
    return { tone: 'warning', word: runtimeState === 'stopping' ? 'Stopping' : 'Starting', Icon: Loader2, spin: true, full }
  }
  if (runtimeState === 'running' && health === 'healthy') {
    return { tone: 'positive', word: 'Healthy', Icon: CircleCheck, spin: false, full }
  }
  if (runtimeState === 'stopped' || health === 'stopped') {
    return { tone: 'neutral', word: 'Stopped', Icon: CirclePause, spin: false, full }
  }
  if (runtimeState === 'disabled' || health === 'disabled') {
    return { tone: 'neutral', word: 'Disabled', Icon: CircleMinus, spin: false, full }
  }
  return { tone: 'neutral', word: 'Unknown', Icon: CircleHelp, spin: false, full }
}

export function HealthSignal({ runtimeState, health, size = 'md' }: {
  runtimeState: ProjectRuntimeState
  health: HealthState
  size?: 'md' | 'lg'
}) {
  const { tone, word, Icon, spin, full } = healthSignal(runtimeState, health)
  return (
    <span className={`health-signal health-signal--${tone} health-signal--${size}`} title={full} aria-label={full}>
      <Icon aria-hidden="true" size={size === 'lg' ? 16 : 14} className={spin ? 'health-signal__spin' : undefined} />
      <span>{word}</span>
    </span>
  )
}
