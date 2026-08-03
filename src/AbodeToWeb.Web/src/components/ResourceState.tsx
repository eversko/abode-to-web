import { AlertTriangle, RefreshCw, WifiOff } from 'lucide-react'
import type { ReactNode } from 'react'
import { Button } from './Button'

export function LoadingState({ label = 'Loading' }: { label?: string }) {
  return (
    <div className="loading-state" role="status" aria-live="polite">
      <span className="loading-state__mark" aria-hidden="true" />
      <span>{label}</span>
    </div>
  )
}

export function ErrorState({ error, retry, title = 'Abode to Web could not load this view' }: { error: Error; retry?: () => void; title?: string }) {
  const offline = !navigator.onLine || /unreachable|network|fetch/i.test(error.message)
  const Icon = offline ? WifiOff : AlertTriangle
  return (
    <section className="error-state" role="alert">
      <span className="error-state__icon"><Icon aria-hidden="true" size={22} /></span>
      <div>
        <h2>{offline ? 'Abode to Web is offline' : title}</h2>
        <p>{error.message}</p>
        {retry && <Button icon={RefreshCw} onClick={retry}>Try again</Button>}
      </div>
    </section>
  )
}

export function EmptyState({ icon, title, children, action }: { icon: ReactNode; title: string; children: ReactNode; action?: ReactNode }) {
  return (
    <section className="empty-state">
      <div className="empty-state__icon" aria-hidden="true">{icon}</div>
      <h2>{title}</h2>
      <p>{children}</p>
      {action}
    </section>
  )
}
