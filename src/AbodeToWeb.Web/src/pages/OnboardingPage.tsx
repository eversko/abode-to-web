import { ArrowLeft, ArrowRight, Check, CheckCircle2, CircleAlert, ClipboardCopy, Cloud, FolderGit2, LoaderCircle, ShieldCheck } from 'lucide-react'
import { useMemo, useState, type SyntheticEvent } from 'react'
import { Link } from 'react-router'
import { useApi } from '../api/context'
import type { AppVisibility, CreateProjectRequest, OnboardingDetection, OnboardingSource, OnboardingValidation } from '../api/schema'
import { Button } from '../components/Button'
import { useOperations } from '../operations/OperationContext'
import { sentenceCase } from '../lib/format'

type WizardStep = 'source' | 'review' | 'configure' | 'validate' | 'started'
type CommandSelections = { install?: string; build?: string; start?: string }
type SaveState = 'pending' | 'succeeded' | 'failed' | 'cancelled'

const stepIndex: Record<WizardStep, number> = { source: 0, review: 1, configure: 2, validate: 3, started: 4 }
const agentQuickstartUrl = 'https://github.com/eversko/abode-to-web/blob/main/docs/AGENT-QUICKSTART.md'
const agentAssessPrompt = `Read ${agentQuickstartUrl} and follow it for this repository. Start with ASSESS only: inspect read-only, report whether the project fits Abode to Web, and show the exact plan. Do not install, elevate, edit files, handle secrets, deploy, publish, or change DNS, firewall, router, or power settings. Stop and wait for my explicit APPROVE LOCAL SETUP message. After local verification, stop again and wait for my separate APPROVE PUBLISH message.`

export function OnboardingPage() {
  const api = useApi()
  const { runOperation } = useOperations()
  const [step, setStep] = useState<WizardStep>('source')
  const [sourceKind, setSourceKind] = useState<'existing' | 'github'>('existing')
  const [sourceValue, setSourceValue] = useState('')
  const [detection, setDetection] = useState<OnboardingDetection>()
  const [commands, setCommands] = useState<CommandSelections>({})
  const [request, setRequest] = useState<CreateProjectRequest>()
  const [validation, setValidation] = useState<OnboardingValidation>()
  const [cloneDestination, setCloneDestination] = useState<string>()
  const [saveState, setSaveState] = useState<SaveState>('pending')
  const [saveFailureMessage, setSaveFailureMessage] = useState<string>()
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string>()

  const detect = async (event: SyntheticEvent<HTMLFormElement>) => {
    event.preventDefault()
    const source: OnboardingSource = sourceKind === 'existing' ? { kind: 'existing', path: sourceValue } : { kind: 'github', repositoryUrl: sourceValue }
    setBusy(true)
    setError(undefined)
    try {
      if (source.kind === 'github') {
        const accepted = await api.cloneProject(source.repositoryUrl)
        const tracked = await runOperation(
          Promise.resolve(accepted),
          `Clone ${source.repositoryUrl}`,
          () => { void inspectClonedRepository(accepted.repositoryPath) },
          (progress) => {
            setCloneDestination(undefined)
            setError(progress.status === 'cancelled'
              ? 'The clone was cancelled. Check the repository name and try again.'
              : `The clone did not complete. ${progress.message}`)
          },
        )
        if (tracked) setCloneDestination(accepted.repositoryPath)
        return
      }
      const result = await api.detectProject(source)
      setCommands({})
      setDetection(result)
      setStep('review')
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Abode to Web could not inspect the repository.')
    } finally {
      setBusy(false)
    }
  }

  const inspectClonedRepository = async (repositoryPath: string) => {
    setBusy(true)
    setError(undefined)
    try {
      const result = await api.detectProject({ kind: 'existing', path: repositoryPath })
      setSourceKind('existing')
      setSourceValue(repositoryPath)
      setCommands({})
      setDetection(result)
      setStep('review')
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'The clone finished, but Abode to Web could not inspect it.')
    } finally {
      setBusy(false)
    }
  }

  const continueToConfiguration = () => {
    if (!detection) return
    const name = detection.repositoryPath.replaceAll('\\', '/').split('/').filter(Boolean).at(-1) ?? 'New project'
    setRequest({
      detectionId: detection.detectionId,
      id: name.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/(^-|-$)/g, ''),
      name,
      description: '',
      repositoryProvider: detection.suggestedRepositoryProvider ?? null,
      repositoryRemote: detection.suggestedRemote ?? '',
      repositoryBranch: detection.suggestedBranch ?? '',
      installSuggestionId: commands.install ?? null,
      buildSuggestionId: commands.build ?? null,
      startSuggestionId: commands.start ?? null,
      staticOutputDirectory: detection.suggestedStaticOutputDirectory ?? null,
      portEnvironmentVariable: detection.suggestedPortEnvironmentVariable ?? '',
      healthPath: detection.suggestedHealthPaths[0] ?? '/',
      internalPort: detection.suggestedPort,
      hostname: '',
      accessApplicationAudience: null,
      visibility: 'disabled',
      autostart: false,
      protected: false,
      runTestBuild: false,
    })
    setStep('configure')
  }

  const validate = async (event: SyntheticEvent<HTMLFormElement>) => {
    event.preventDefault()
    if (!request) return
    setBusy(true)
    setError(undefined)
    try {
      const result = await api.validateProject(request)
      setValidation(result)
      setStep('validate')
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Abode to Web could not validate the configuration.')
    } finally {
      setBusy(false)
    }
  }

  const save = async () => {
    if (!request) return
    setBusy(true)
    setSaveState('pending')
    setSaveFailureMessage(undefined)
    const accepted = await runOperation(
      api.createProject(request),
      `Add ${request.name}`,
      () => setSaveState('succeeded'),
      (progress) => {
        setSaveState(progress.status === 'cancelled' ? 'cancelled' : 'failed')
        setSaveFailureMessage(progress.message)
      },
    )
    setBusy(false)
    if (accepted) setStep('started')
  }

  return (
    <div className="onboarding-layout">
      <aside className="wizard-rail" aria-label="Add project progress">
        <Link to="/projects" className="back-link"><ArrowLeft aria-hidden="true" size={15} />Projects</Link>
        <span className="eyebrow">Guided setup</span>
        <h2>Add a project</h2>
        <ol>
          {['Repository', 'Detection', 'Configuration', 'Validation', 'Save'].map((label, index) => <li key={label} className={index === stepIndex[step] ? 'is-current' : index < stepIndex[step] ? 'is-complete' : ''} aria-current={index === stepIndex[step] ? 'step' : undefined}>
            <span>{index < stepIndex[step] ? <Check aria-hidden="true" size={13} /> : index + 1}</span>{label}
          </li>)}
        </ol>
        <p>Abode to Web only inspects repositories inside configured project roots. It never scans the rest of your PC.</p>
      </aside>
      <section className="wizard-panel">
        {error && <div className="inline-alert inline-alert--error" role="alert">{error}</div>}
        {step === 'source' && <SourceStep sourceKind={sourceKind} setSourceKind={(kind) => { setSourceKind(kind); setCloneDestination(undefined) }} sourceValue={sourceValue} setSourceValue={setSourceValue} cloneDestination={cloneDestination} busy={busy} onSubmit={(event) => { void detect(event) }} />}
        {step === 'review' && detection && <ReviewStep detection={detection} commands={commands} setCommands={setCommands} onBack={() => setStep('source')} onContinue={continueToConfiguration} />}
        {step === 'configure' && request && <ConfigureStep request={request} setRequest={setRequest} busy={busy} onBack={() => setStep('review')} onSubmit={(event) => { void validate(event) }} />}
        {step === 'validate' && request && validation && <ValidationStep request={request} validation={validation} busy={busy} onBack={() => setStep('configure')} onSave={() => { void save() }} />}
        {step === 'started' && request && <StartedStep name={request.name} id={request.id} state={saveState} runTestBuild={request.runTestBuild} failureMessage={saveFailureMessage} />}
      </section>
    </div>
  )
}

type SourceProps = {
  sourceKind: 'existing' | 'github'
  setSourceKind: (kind: 'existing' | 'github') => void
  sourceValue: string
  setSourceValue: (value: string) => void
  cloneDestination?: string
  busy: boolean
  onSubmit: (event: SyntheticEvent<HTMLFormElement>) => void
}

function SourceStep({ sourceKind, setSourceKind, sourceValue, setSourceValue, cloneDestination, busy, onSubmit }: SourceProps) {
  const [copyState, setCopyState] = useState<'idle' | 'copied' | 'error'>('idle')
  const [showManualSetup, setShowManualSetup] = useState(false)
  const copyPrompt = async () => {
    try {
      const clipboard = Reflect.get(navigator, 'clipboard') as Clipboard | undefined
      if (clipboard === undefined) throw new Error('Clipboard access is unavailable.')
      await clipboard.writeText(agentAssessPrompt)
      setCopyState('copied')
    } catch {
      setCopyState('error')
    }
  }
  return <form onSubmit={onSubmit}>
    <section className="agent-handoff" aria-labelledby="agent-handoff-title">
      <div className="agent-handoff__heading">
        <span className="agent-handoff__icon" aria-hidden="true"><ClipboardCopy size={22} /></span>
        <div><span className="eyebrow">Agent handoff</span><h2 id="agent-handoff-title">Give this to your LLM</h2></div>
      </div>
      <p className="wizard-lead">Copy one safe instruction into your coding assistant. It starts read-only and stops before every meaningful change.</p>
      <div className="agent-handoff__actions"><Button variant="primary" type="button" onClick={() => { void copyPrompt() }}><ClipboardCopy aria-hidden="true" size={15} />Copy prompt</Button><a className="button button--quiet" href={agentQuickstartUrl} target="_blank" rel="noreferrer">Open agent guide</a></div>
      <div className="agent-handoff__status" aria-live="polite" aria-atomic="true">
        {copyState === 'copied' && <div className="inline-alert" role="status"><CheckCircle2 aria-hidden="true" size={17} />Prompt copied. Paste it into your LLM from the project repository.</div>}
        {copyState === 'error' && <div className="inline-alert inline-alert--error" role="alert"><CircleAlert aria-hidden="true" size={17} />Could not copy automatically. Open the agent guide and copy the prompt manually.</div>}
      </div>
      <div className="approval-handoff">
        <div className="approval-handoff__heading"><ShieldCheck aria-hidden="true" size={17} /><div><strong>Three deliberate gates</strong><span>Your LLM must stop and wait at each boundary.</span></div></div>
        <ol aria-label="Approval gates">
          <li><span>01</span><div><strong>ASSESS</strong><small>Read-only fit check</small></div></li>
          <li><span>02</span><div><strong>APPROVE LOCAL SETUP</strong><small>Reviewed repository work</small></div></li>
          <li><span>03</span><div><strong>APPROVE PUBLISH</strong><small>Exact commit deployment</small></div></li>
        </ol>
      </div>
      <details className="prompt-preview"><summary>Preview exact prompt</summary><p><code>{agentAssessPrompt}</code></p></details>
    </section>
    {!showManualSetup && <div className="manual-setup-cta"><div><strong>Prefer to configure it yourself?</strong><span>The dashboard wizard remains available.</span></div><Button variant="quiet" type="button" onClick={() => setShowManualSetup(true)}>Set up manually in dashboard</Button></div>}
    {showManualSetup && <div className="manual-setup-form">
      <h3>Manual dashboard setup</h3><p className="wizard-lead">Choose an existing local Git repository, or clone one from GitHub using the sign-in configured for the Abode to Web service identity.</p>
      <fieldset className="choice-grid"><legend className="sr-only">Repository source</legend>
        <label className={`choice-card ${sourceKind === 'existing' ? 'choice-card--selected' : ''}`}><input type="radio" name="source" checked={sourceKind === 'existing'} onChange={() => { setSourceKind('existing'); setSourceValue('') }} /><FolderGit2 aria-hidden="true" /><strong>Existing repository</strong><span>Use a project already on this PC.</span></label>
        <label className={`choice-card ${sourceKind === 'github' ? 'choice-card--selected' : ''}`}><input type="radio" name="source" checked={sourceKind === 'github'} onChange={() => { setSourceKind('github'); setSourceValue('') }} /><Cloud aria-hidden="true" /><strong>Clone from GitHub</strong><span>Clone into an allowed project root.</span></label>
      </fieldset>
      <label className="field"><span>{sourceKind === 'existing' ? 'Repository path' : 'GitHub repository'}</span><input required value={sourceValue} onChange={(event) => setSourceValue(event.target.value)} placeholder={sourceKind === 'existing' ? 'C:/Projects/my-app' : 'owner/repository'} /></label>
      <p className="field-hint">{sourceKind === 'existing' ? 'The path must resolve inside a project root configured in Abode to Web.' : 'Abode to Web delegates authentication to GitHub CLI or Git Credential Manager.'}</p>
      {cloneDestination && <div className="inline-alert" role="status"><LoaderCircle className="spin" aria-hidden="true" size={17} />Clone accepted for <code>{cloneDestination}</code>. Abode to Web will inspect it automatically after the clone operation succeeds.</div>}
      <div className="wizard-actions"><Link className="button button--quiet" to="/projects">Cancel</Link><Button variant="primary" busy={busy} type="submit">{sourceKind === 'existing' ? 'Inspect repository' : 'Clone repository'} <ArrowRight aria-hidden="true" size={15} /></Button></div>
    </div>}
  </form>
}

type ReviewProps = { detection: OnboardingDetection; commands: CommandSelections; setCommands: (commands: CommandSelections) => void; onBack: () => void; onContinue: () => void }

function ReviewStep({ detection, commands, setCommands, onBack, onContinue }: ReviewProps) {
  const byPurpose = useMemo(() => ['install', 'build', 'start'].map((purpose) => detection.suggestedCommands.find((command) => command.purpose === purpose)).filter(Boolean), [detection])
  return <div>
    <span className="eyebrow">Step 2 of 5</span><h2>Review detection</h2><p className="wizard-lead">The stable path serves committed, prebuilt static output. Leave every project command unchecked; the saved manifest will contain no install, build, or start command.</p>
    <div className="detection-summary"><div><span>Framework</span><strong>{sentenceCase(detection.framework)}</strong></div><div><span>Package manager</span><strong>{detection.packageManager ?? 'Not detected'}</strong></div><div><span>Lockfile</span><strong>{detection.lockFile ?? 'None'}</strong></div><div><span>Available port</span><strong>{detection.suggestedPort}</strong></div></div>
    <div className="detection-summary" aria-label="Repository and runtime suggestions"><div><span>Provider suggestion</span><strong>{detection.suggestedRepositoryProvider ? sentenceCase(detection.suggestedRepositoryProvider) : 'Choose during review'}</strong></div><div><span>Git remote</span><strong>{detection.suggestedRemote ?? 'Enter manually'}</strong></div><div><span>Current branch</span><strong>{detection.suggestedBranch ?? 'Enter manually'}</strong></div><div><span>Port variable</span><strong>{detection.suggestedPortEnvironmentVariable ?? 'Enter manually'}</strong></div>{detection.suggestedStaticOutputDirectory != null && <div><span>Static output</span><strong>{detection.suggestedStaticOutputDirectory}</strong></div>}</div>
    {detection.gitRemotes.length > 1 && <p className="field-hint">Detected remote names: {detection.gitRemotes.join(', ')}. Remote URLs and credentials are never returned to the dashboard.</p>}
    <fieldset className="suggestion-list"><legend>Experimental project command suggestions</legend>
      {byPurpose.map((suggestion) => suggestion && <label className="suggestion-card" key={suggestion.id}>
        <input type="checkbox" checked={commands[suggestion.purpose] === suggestion.id} onChange={(event) => setCommands({ ...commands, [suggestion.purpose]: event.target.checked ? suggestion.id : undefined })} />
        <span className="suggestion-card__check"><Check aria-hidden="true" size={13} /></span>
        <span><strong>Use {suggestion.purpose} suggestion</strong><code>{suggestion.executable} {suggestion.arguments.join(' ')}</code><small>{suggestion.reason}</small></span>
      </label>)}
    </fieldset>
    {byPurpose.length > 0 && <div className="inline-alert"><CircleAlert aria-hidden="true" size={17} />Selecting any command requires <code>abodeToWeb.experimentalProjectExecution: true</code>. Every command-executing project uses the shared <code>AbodeToWebRuntime</code> Windows identity, so all such projects belong to one Windows trust domain. In v0.1, these releases are fresh-deploy only; retained restart, rollback, and recovery are unavailable.</div>}
    {detection.warnings.map((warning) => <div className="inline-alert" key={warning}>{warning}</div>)}
    <div className="wizard-actions"><Button variant="quiet" onClick={onBack}><ArrowLeft aria-hidden="true" size={15} />Back</Button><Button variant="primary" onClick={onContinue}>Configure project <ArrowRight aria-hidden="true" size={15} /></Button></div>
  </div>
}

type ConfigureProps = { request: CreateProjectRequest; setRequest: (request: CreateProjectRequest) => void; busy: boolean; onBack: () => void; onSubmit: (event: SyntheticEvent<HTMLFormElement>) => void }

function ConfigureStep({ request, setRequest, busy, onBack, onSubmit }: ConfigureProps) {
  const update = <K extends keyof CreateProjectRequest>(key: K, value: CreateProjectRequest[K]) => setRequest({ ...request, [key]: value })
  const hasProjectCommands = request.installSuggestionId !== null || request.buildSuggestionId !== null || request.startSuggestionId !== null
  const updateVisibility = (visibility: AppVisibility) => setRequest({
    ...request,
    visibility,
    accessApplicationAudience: visibility === 'protected' ? request.accessApplicationAudience : null,
  })
  return <form onSubmit={onSubmit}>
    <span className="eyebrow">Step 3 of 5</span><h2>Configure the project</h2><p className="wizard-lead">Review every value. The manifest remains the source of truth after onboarding.</p>
    <div className="form-grid"><label className="field"><span>Name</span><input required value={request.name} onChange={(event) => update('name', event.target.value)} /></label><label className="field"><span>Project ID</span><input required pattern="[a-z0-9-]+" value={request.id} onChange={(event) => update('id', event.target.value)} /></label></div>
    <label className="field"><span>Description <small>optional</small></span><input value={request.description ?? ''} onChange={(event) => update('description', event.target.value)} /></label>
    <div className="form-grid"><label className="field"><span>Repository provider</span><select required value={request.repositoryProvider ?? ''} onChange={(event) => update('repositoryProvider', event.target.value === '' ? null : event.target.value as 'github' | 'other')}><option value="" disabled>Choose provider</option><option value="github">GitHub</option><option value="other">Other Git remote</option></select></label><label className="field"><span>Git remote name</span><input required value={request.repositoryRemote ?? ''} onChange={(event) => update('repositoryRemote', event.target.value)} placeholder="origin" /></label></div>
    <div className="form-grid"><label className="field"><span>Deployment branch</span><input required value={request.repositoryBranch ?? ''} onChange={(event) => update('repositoryBranch', event.target.value)} placeholder="main" /></label><label className="field"><span>Port environment variable</span><input required pattern="[A-Za-z_][A-Za-z0-9_]*" value={request.portEnvironmentVariable ?? ''} onChange={(event) => update('portEnvironmentVariable', event.target.value)} placeholder="PORT" /></label></div>
    {request.staticOutputDirectory !== null && <label className="field"><span>Committed static output directory</span><input required value={request.staticOutputDirectory} onChange={(event) => update('staticOutputDirectory', event.target.value)} placeholder="dist" /></label>}
    <p className="field-hint">Stable mode requires this output directory to exist in the exact Git commit. Abode to Web will not install dependencies, build, or start project code. Confirm the remote, branch, output directory, and port variable.</p>
    {hasProjectCommands && <div className="inline-alert"><CircleAlert aria-hidden="true" size={17} />Experimental project execution is selected. The machine manifest must explicitly enable <code>experimentalProjectExecution</code>, and every command-executing project shares one Windows trust domain. In v0.1, these releases are fresh-deploy only; retained restart, rollback, and recovery are unavailable.</div>}
    <div className="form-grid"><label className="field"><span>Public hostname {request.visibility === 'disabled' && <small>optional while disabled</small>}</span><input required={request.visibility !== 'disabled'} placeholder="demo.example.com" value={request.hostname} onChange={(event) => update('hostname', event.target.value)} /></label><label className="field"><span>Internal port</span><input required type="number" min={1024} max={65535} value={request.internalPort ?? ''} onChange={(event) => update('internalPort', Number(event.target.value))} /></label></div>
    <div className="form-grid"><label className="field"><span>Health path</span><input value={request.healthPath ?? ''} onChange={(event) => update('healthPath', event.target.value)} /></label><label className="field"><span>Visibility</span><select value={request.visibility} onChange={(event) => updateVisibility(event.target.value as AppVisibility)}><option value="disabled">Disabled</option><option value="public">Public</option><option value="protected">Protected by access policy</option></select></label></div>
    <p className="field-hint">Keep visibility disabled during local setup. Publishing a hostname is a separate approval after you review the exact commit, health gate, and rollback point.</p>
    {request.visibility === 'protected' && <label className="field"><span>Cloudflare Access application audience</span><input required autoComplete="off" value={request.accessApplicationAudience ?? ''} onChange={(event) => update('accessApplicationAudience', event.target.value)} /></label>}
    {request.visibility === 'protected' && <p className="field-hint">Copy the Application Audience (AUD) tag from the Cloudflare Access application that protects this hostname.</p>}
    <div className="toggle-list"><label aria-label="Start with Abode to Web"><input type="checkbox" checked={request.autostart} onChange={(event) => update('autostart', event.target.checked)} /><span><strong>Start with Abode to Web</strong><small>Staggered with other autostart projects.</small></span></label><label aria-label="Protect from bulk stop"><input type="checkbox" checked={request.protected} onChange={(event) => update('protected', event.target.checked)} /><span><strong>Protect from bulk stop</strong><small>Quiet Mode will leave this application running.</small></span></label><label aria-label="Run a test build"><input type="checkbox" checked={request.runTestBuild} onChange={(event) => update('runTestBuild', event.target.checked)} /><span><strong>Run a test build</strong><small>{hasProjectCommands ? 'Experimental: this will execute the selected project commands in a candidate.' : 'Optional: validates the committed static output without running a project command.'}</small></span></label></div>
    <div className="wizard-actions"><Button variant="quiet" onClick={onBack}><ArrowLeft aria-hidden="true" size={15} />Back</Button><Button variant="primary" busy={busy} type="submit">Validate configuration <ArrowRight aria-hidden="true" size={15} /></Button></div>
  </form>
}

function ValidationStep({ request, validation, busy, onBack, onSave }: { request: CreateProjectRequest; validation: OnboardingValidation; busy: boolean; onBack: () => void; onSave: () => void }) {
  const selectedCommands = [request.installSuggestionId, request.buildSuggestionId, request.startSuggestionId].filter((value) => value !== null)
  const hasProjectCommands = selectedCommands.length > 0
  let validAction = 'Saving will write the manifest atomically. Nothing will execute or be published.'
  if (request.runTestBuild && hasProjectCommands) validAction = 'Saving will write the manifest and execute the selected experimental project commands in a candidate. Routing remains controlled by visibility.'
  else if (request.runTestBuild) validAction = 'Saving will write the manifest and validate the committed static output without running a project command. Routing remains controlled by visibility.'
  else if (hasProjectCommands) validAction = 'Saving will persist the experimental project commands, but will not run them now. Future execution requires the machine-wide experimentalProjectExecution opt-in.'
  return <div>
    <span className="eyebrow">Step 4 of 5</span><h2>{validation.isValid ? 'Configuration is valid' : 'Configuration needs changes'}</h2><p className="wizard-lead">Abode to Web validated paths, hostname, port, selected commands, and manifest structure.</p>
    <div className={`validation-hero ${validation.isValid ? 'validation-hero--valid' : ''}`}><ShieldCheck aria-hidden="true" /><div><strong>{validation.isValid ? (request.runTestBuild ? 'Ready to save and test' : 'Ready to save') : 'Not ready to save'}</strong><span>{validation.isValid ? validAction : 'Return to configuration and resolve each error.'}</span></div></div>
    {validation.issues.length > 0 && <ul className="issue-list">{validation.issues.map((issue) => <li key={`${issue.path}-${issue.code}`}><strong>{issue.path}</strong><span>{issue.message}</span></li>)}</ul>}
    <dl className="review-list"><div><dt>Project</dt><dd>{request.name}</dd></div><div><dt>Repository</dt><dd>{sentenceCase(request.repositoryProvider ?? 'not selected')} · {request.repositoryRemote}/{request.repositoryBranch}</dd></div><div><dt>Hostname</dt><dd>{request.hostname || 'Not configured'}</dd></div><div><dt>Port</dt><dd>{request.internalPort} via <code>{request.portEnvironmentVariable}</code></dd></div>{request.staticOutputDirectory !== null && <div><dt>Committed static output</dt><dd><code>{request.staticOutputDirectory}</code></dd></div>}<div><dt>Project execution</dt><dd>{hasProjectCommands ? 'Experimental commands selected' : 'None — stable static path'}</dd></div><div><dt>Visibility</dt><dd>{sentenceCase(request.visibility)}</dd></div>{request.visibility === 'protected' && <div><dt>Access audience</dt><dd><code>{request.accessApplicationAudience}</code></dd></div>}<div><dt>Test build</dt><dd>{request.runTestBuild ? 'Will run' : 'Will not run'}</dd></div></dl>
    <div className="wizard-actions"><Button variant="quiet" onClick={onBack}><ArrowLeft aria-hidden="true" size={15} />Back</Button><Button variant="primary" disabled={!validation.isValid} busy={busy} onClick={onSave}>{request.runTestBuild ? 'Save and test' : 'Save project'} <ArrowRight aria-hidden="true" size={15} /></Button></div>
  </div>
}

function StartedStep({ name, id, state, runTestBuild, failureMessage }: { name: string; id: string; state: SaveState; runTestBuild: boolean; failureMessage?: string }) {
  const succeeded = state === 'succeeded'
  const stopped = state === 'failed' || state === 'cancelled'
  const heading = succeeded ? `${name} was added` : stopped ? `${name} setup ${state === 'cancelled' ? 'was cancelled' : 'did not complete'}` : `${name} was accepted`
  const detail = succeeded
    ? runTestBuild
      ? 'Abode to Web saved the reviewed manifest and completed the requested test operation.'
      : 'Abode to Web saved the reviewed manifest. Nothing was built or published.'
    : stopped
      ? `The operation stopped before setup completed. ${failureMessage ?? 'Open the project or operation details for the safe failure reason.'}`
      : runTestBuild
        ? 'Abode to Web accepted the request. Follow progress while the service saves the manifest, builds the isolated candidate, and checks health.'
        : 'Abode to Web accepted the save request. Follow progress while the service writes the reviewed manifest.'
  const statusDetail = succeeded
    ? runTestBuild ? 'The requested test completed successfully.' : 'The project is saved with no test build or publication.'
    : stopped
      ? 'The manifest may already exist. Open the project to review the safe failure and current state.'
      : 'You can leave this page. The Windows service owns the in-progress operation.'
  return <div className="wizard-complete"><span className="wizard-complete__icon">{succeeded ? <CheckCircle2 aria-hidden="true" /> : stopped ? <CircleAlert aria-hidden="true" /> : <LoaderCircle className="spin" aria-hidden="true" />}</span><span className="eyebrow">Step 5 of 5</span><h2>{heading}</h2><p>{detail}</p><div className={`inline-alert ${stopped ? 'inline-alert--error' : ''}`}>{succeeded ? <CheckCircle2 aria-hidden="true" size={17} /> : stopped ? <CircleAlert aria-hidden="true" size={17} /> : <LoaderCircle className="spin" aria-hidden="true" size={17} />}{statusDetail}</div><div className="wizard-actions"><Link className="button button--quiet" to="/projects">All projects</Link><Link className="button button--primary" to={`/projects/${id}`}>{stopped ? 'Review project' : 'Open project'} <ArrowRight aria-hidden="true" size={15} /></Link></div></div>
}
