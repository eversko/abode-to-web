export type CardClass = 'abode-to-web' | 'external' | 'local' | 'planned'

const labels: Record<CardClass, string> = {
  'abode-to-web': 'Abode to Web',
  external: 'External',
  local: 'Local',
  planned: 'Planned',
}

export function CardBadge({ kind }: { kind: CardClass }) {
  return <span className={`card-badge card-badge--${kind}`}>{labels[kind]}</span>
}
