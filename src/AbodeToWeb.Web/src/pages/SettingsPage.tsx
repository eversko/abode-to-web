import { FileCheck2, Globe2, Moon, Save, Sparkles } from 'lucide-react'
import { useCallback, useState } from 'react'
import { useApi } from '../api/context'
import { Button } from '../components/Button'
import { ConfirmDialog } from '../components/ConfirmDialog'
import { ErrorState, LoadingState } from '../components/ResourceState'
import { useResource } from '../hooks/useResource'
import { useOperations } from '../operations/OperationContext'

export function SettingsPage() {
  const api = useApi()
  const { runOperation } = useOperations()
  const load = useCallback((signal: AbortSignal) => api.getOverview(signal), [api])
  const { data, error, loading, reload } = useResource(load)
  const [quietConfirm, setQuietConfirm] = useState(false)
  if (loading && !data) return <LoadingState label="Loading settings" />
  if (error && !data) return <ErrorState error={error} retry={reload} />
  if (!data) return null
  const quiet = data.status.quietMode
  const applyQuiet = () => { setQuietConfirm(false); void runOperation(quiet ? api.startShowcase() : api.setQuietMode(true), quiet ? 'Start showcase' : 'Enter quiet mode', reload) }
  return <div className="page-stack"><section className="page-intro"><div><span className="eyebrow">Control plane</span><h2>Settings</h2><p>Safe operating modes and read-only effective configuration.</p></div></section>
    <div className="settings-grid"><section className="panel setting-card"><span className="setting-card__icon"><Moon aria-hidden="true" /></span><div><h3>Quiet Mode</h3><p>Stop all nonessential demos while preserving protected applications.</p><StatusLine enabled={quiet} enabledLabel="Enabled" disabledLabel="Disabled" /></div><Button onClick={() => setQuietConfirm(true)}>{quiet ? 'Start showcase' : 'Stop all demos'}</Button></section><section className="panel setting-card"><span className="setting-card__icon"><Sparkles aria-hidden="true" /></span><div><h3>Showcase</h3><p>Start the selected showcase projects with configured startup staggering.</p><span className="setting-note">Protected bulk action</span></div><Button variant="primary" onClick={() => { void runOperation(api.startShowcase(), 'Start showcase', reload) }}>Start showcase</Button></section></div>
    <div className="detail-grid"><section className="panel"><div className="panel__heading"><div><span className="eyebrow">Private listener</span><h3>Dashboard</h3></div><FileCheck2 aria-hidden="true" /></div><dl className="review-list"><div><dt>Address</dt><dd>127.0.0.1:4545</dd></div><div><dt>Remote access</dt><dd>Disabled</dd></div><div><dt>Authentication</dt><dd>Local browser</dd></div><div><dt>Configuration</dt><dd>Last known good</dd></div></dl><div className="inline-alert"><Save aria-hidden="true" size={17} />Manifest changes are validated completely and written atomically.</div></section><section className="panel"><div className="panel__heading"><div><span className="eyebrow">Application traffic</span><h3>Gateway</h3></div><Globe2 aria-hidden="true" /></div><dl className="review-list"><div><dt>Address</dt><dd>127.0.0.1:4580</dd></div><div><dt>Unknown hostnames</dt><dd>Rejected</dd></div><div><dt>Dashboard exposure</dt><dd>Blocked</dd></div><div><dt>Public TLS</dt><dd>Cloudflare</dd></div></dl><p className="muted-copy">Public TLS terminates at Cloudflare. No inbound router ports are required.</p></section></div>
    <ConfirmDialog open={quietConfirm} title={quiet ? 'Start the showcase?' : 'Stop all nonessential demos?'} confirmLabel={quiet ? 'Start showcase' : 'Stop all demos'} onCancel={() => setQuietConfirm(false)} onConfirm={applyQuiet}><p>{quiet ? 'Abode to Web will leave Quiet Mode and start projects in the showcase group with configured staggering.' : 'Protected applications remain running. Other demos can be restarted individually or with Start Showcase.'}</p></ConfirmDialog>
  </div>
}

function StatusLine({ enabled, enabledLabel, disabledLabel }: { enabled: boolean; enabledLabel: string; disabledLabel: string }) {
  return <span className={`setting-status ${enabled ? 'setting-status--enabled' : ''}`}><span aria-hidden="true" />{enabled ? enabledLabel : disabledLabel}</span>
}
