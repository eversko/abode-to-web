export const formatBytes = (bytes: number) => {
  if (bytes <= 0) return '0 GB'
  return `${(bytes / 1024 ** 3).toFixed(bytes < 10 * 1024 ** 3 ? 1 : 0)} GB`
}

export const formatPercent = (value: number) => `${Math.round(value)}%`

export const formatRelativeTime = (value?: string | null) => {
  if (!value) return 'Never'
  const elapsed = Date.now() - new Date(value).getTime()
  const minutes = Math.max(0, Math.floor(elapsed / 60_000))
  if (minutes < 1) return 'Just now'
  if (minutes < 60) return `${minutes}m ago`
  const hours = Math.floor(minutes / 60)
  if (hours < 24) return `${hours}h ago`
  const days = Math.floor(hours / 24)
  return days < 30 ? `${days}d ago` : new Intl.DateTimeFormat(undefined, { month: 'short', day: 'numeric' }).format(new Date(value))
}

export const formatDateTime = (value?: string | null) => value
  ? new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' }).format(new Date(value))
  : '—'

export const sentenceCase = (value: string) => value
  .replace(/([A-Z])/g, ' $1')
  .replace(/^./, (character) => character.toUpperCase())

export const projectMonogram = (name: string) => name
  .split(/\s+/)
  .slice(0, 2)
  .map((word) => word[0])
  .join('')
  .toUpperCase()
