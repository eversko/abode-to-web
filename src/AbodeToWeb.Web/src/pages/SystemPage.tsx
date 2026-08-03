import { CheckCircle2, Cloud, Cpu, Database, ExternalLink, HardDrive, Network, Server, ShieldCheck } from 'lucide-react'
import { useCallback } from 'react'
import { useApi } from '../api/context'
import { ErrorState, LoadingState } from '../components/ResourceState'
import { StatusPill } from '../components/StatusPill'
import { useResource } from '../hooks/useResource'
import { formatBytes, formatDateTime, formatPercent, sentenceCase } from '../lib/format'
import { LogViewer } from '../logs/LogViewer'

export function SystemPage() {
  const api = useApi()
  const load = useCallback(async (signal: AbortSignal) => {
    const [overview, tunnel] = await Promise.all([api.getOverview(signal), api.getTunnelStatus(signal)])
    return { status: overview.status, tunnel }
  }, [api])
  const { data, error, loading, reload } = useResource(load)
  if (loading && !data) return <LoadingState label="Running system checks" />
  if (error && !data) return <ErrorState error={error} retry={reload} title="Diagnostics unavailable" />
  if (!data) return null
  const { status, tunnel } = data
  return <div className="page-stack"><section className="page-intro"><div><span className="eyebrow">Diagnostics</span><h2>System</h2><p>Health of this PC, the Abode to Web service, local gateway, and public tunnel.</p></div><button className="button button--secondary" type="button" onClick={reload}>Run checks again</button></section>
    <div className="diagnostic-grid"><DiagnosticCard icon={Server} name="Abode to Web service" label={status.abodeToWeb.label} condition={status.abodeToWeb.condition} detail={status.abodeToWeb.detail} /><DiagnosticCard icon={Network} name="Public gateway" label={status.gateway.label} condition={status.gateway.condition} detail={status.gateway.detail} /><DiagnosticCard icon={Cloud} name="Cloudflare Tunnel" label={status.cloudflareTunnel.label} condition={status.cloudflareTunnel.condition} detail={status.cloudflareTunnel.detail} /><DiagnosticCard icon={ShieldCheck} name="PC" label={status.pc.label} condition={status.pc.condition} detail={status.pc.detail} /></div>
    <div className="detail-grid"><section className="panel"><div className="panel__heading"><div><span className="eyebrow">This PC</span><h3>Capacity</h3></div><Cpu aria-hidden="true" /></div><dl className="review-list"><div><dt>CPU use</dt><dd>{formatPercent(status.cpuPercent)}</dd></div><div><dt>Memory use</dt><dd>{formatBytes(status.memoryUsedBytes)} of {formatBytes(status.memoryTotalBytes)}</dd></div><div><dt>Disk free</dt><dd>{formatBytes(status.diskFreeBytes)} of {formatBytes(status.diskTotalBytes)}</dd></div><div><dt>Observed</dt><dd>{formatDateTime(status.observedAt)}</dd></div></dl><div className="inline-alert"><HardDrive aria-hidden="true" size={17} />Run the repair diagnostics regularly to catch low disk space before it affects deployments or logs.</div></section>
      <section className="panel"><div className="panel__heading"><div><span className="eyebrow">Public connection</span><h3>Cloudflare Tunnel</h3></div><StatusPill value={tunnel.state === 'connected' ? 'connected' : tunnel.state === 'degraded' ? 'degraded' : 'unknown'} label={sentenceCase(tunnel.state)} /></div><dl className="review-list"><div><dt>cloudflared</dt><dd>{tunnel.cloudflaredInstalled ? tunnel.version : 'Not installed'}</dd></div><div><dt>Windows service</dt><dd>{tunnel.serviceInstalled ? tunnel.serviceRunning ? 'Running' : 'Stopped' : 'Not installed'}</dd></div><div><dt>Gateway reachable</dt><dd>{tunnel.gatewayReachable ? 'Yes' : 'No'}</dd></div><div><dt>Tunnel</dt><dd>{tunnel.tunnelName ?? 'Not configured'}</dd></div></dl>{tunnel.safeDiagnostic && <div className="inline-alert">{tunnel.safeDiagnostic}</div>}<p className="muted-copy">Privileged setup and restart actions run through the provided PowerShell scripts, not through this browser.</p></section>
      <section className="panel detail-span"><div className="panel__heading"><div><span className="eyebrow">Storage</span><h3>Runtime data</h3></div><Database aria-hidden="true" /></div><div className="storage-grid"><div><CheckCircle2 aria-hidden="true" /><span><strong>State database</strong><small>%ProgramData%\AbodeToWeb\state\abode-to-web.db</small></span></div><div><CheckCircle2 aria-hidden="true" /><span><strong>Deployment releases</strong><small>%ProgramData%\AbodeToWeb\deployments</small></span></div><div><CheckCircle2 aria-hidden="true" /><span><strong>Rotating logs</strong><small>%ProgramData%\AbodeToWeb\logs</small></span></div><div><CheckCircle2 aria-hidden="true" /><span><strong>Protected secrets</strong><small>%ProgramData%\AbodeToWeb\secrets</small></span></div></div></section>
    </div>
    <LogViewer initialSource="system" availableSources={['system']} title="Abode to Web system logs" />
    <a className="text-link" href="https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/" target="_blank" rel="noreferrer">Cloudflare Tunnel documentation <ExternalLink aria-hidden="true" size={13} /></a>
  </div>
}

function DiagnosticCard({ icon: Icon, name, label, condition, detail }: { icon: typeof Server; name: string; label: string; condition: 'unknown' | 'healthy' | 'degraded' | 'unhealthy' | 'offline'; detail?: string | null }) {
  return <article className="diagnostic-card"><span className="diagnostic-card__icon"><Icon aria-hidden="true" /></span><div><span>{name}</span><strong>{label}</strong><small>{detail ?? 'No detail available'}</small></div><StatusPill value={condition} label={label} /></article>
}
