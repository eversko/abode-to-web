import { Check, ChevronDown, CircleAlert, LoaderCircle, X } from 'lucide-react'
import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useRef,
  useState,
  type PropsWithChildren,
} from 'react'
import type { OperationAccepted, OperationProgress } from '../api/schema'
import { useApi } from '../api/context'

type TrackedOperation = {
  accepted: OperationAccepted
  label: string
  progress: OperationProgress
}

type OperationContextValue = {
  operations: TrackedOperation[]
  runOperation: (
    promise: Promise<OperationAccepted>,
    label: string,
    onSuccess?: () => void,
    onFailure?: (progress: OperationProgress) => void,
  ) => Promise<OperationAccepted | undefined>
  dismissOperation: (operationId: string) => void
}

const OperationContext = createContext<OperationContextValue | null>(null)

const initialProgress = (accepted: OperationAccepted): OperationProgress => ({
  operationId: accepted.operationId,
  kind: accepted.kind,
  status: 'queued',
  stage: 'Queued',
  percent: 0,
  message: 'Abode to Web accepted the operation.',
  timestamp: new Date().toISOString(),
})

export function OperationProvider({ children }: PropsWithChildren) {
  const api = useApi()
  const [operations, setOperations] = useState<TrackedOperation[]>([])
  const watchers = useRef(new Map<string, () => void>())

  useEffect(() => () => {
    watchers.current.forEach((dispose) => dispose())
    watchers.current.clear()
  }, [])

  const runOperation = useCallback(async (
    promise: Promise<OperationAccepted>,
    label: string,
    onSuccess?: () => void,
    onFailure?: (progress: OperationProgress) => void,
  ) => {
    try {
      const accepted = await promise
      setOperations((current) => [{ accepted, label, progress: initialProgress(accepted) }, ...current].slice(0, 5))
      const dispose = api.watchOperation(accepted, {
        onMessage: (progress) => {
          setOperations((current) => current.map((item) => item.accepted.operationId === accepted.operationId ? { ...item, progress } : item))
          if (['succeeded', 'failed', 'cancelled'].includes(progress.status)) {
            watchers.current.get(accepted.operationId)?.()
            watchers.current.delete(accepted.operationId)
            if (progress.status === 'succeeded') onSuccess?.()
            else onFailure?.(progress)
          }
        },
        onError: (error) => {
          const progress: OperationProgress = {
            ...initialProgress(accepted), status: 'failed', stage: 'Progress interrupted',
            message: error.message, errorCode: 'stream_interrupted',
          }
          setOperations((current) => current.map((item) => item.accepted.operationId === accepted.operationId
            ? { ...item, progress }
            : item))
          watchers.current.get(accepted.operationId)?.()
          watchers.current.delete(accepted.operationId)
          onFailure?.(progress)
        },
      })
      watchers.current.set(accepted.operationId, dispose)
      return accepted
    } catch (reason) {
      const message = reason instanceof Error ? reason.message : 'Abode to Web could not start the operation.'
      const accepted: OperationAccepted = { operationId: crypto.randomUUID(), kind: 'validate-configuration', progressUrl: '' }
      const progress: OperationProgress = { ...initialProgress(accepted), status: 'failed', stage: 'Not started', message }
      setOperations((current) => [{
        accepted,
        label,
        progress,
      }, ...current].slice(0, 5))
      return undefined
    }
  }, [api])

  const dismissOperation = useCallback((operationId: string) => {
    watchers.current.get(operationId)?.()
    watchers.current.delete(operationId)
    setOperations((current) => current.filter((item) => item.accepted.operationId !== operationId))
  }, [])

  const value = useMemo(() => ({ operations, runOperation, dismissOperation }), [dismissOperation, operations, runOperation])
  return <OperationContext.Provider value={value}>{children}<OperationDock /></OperationContext.Provider>
}

export function useOperations() {
  const context = useContext(OperationContext)
  if (!context) throw new Error('useOperations must be used inside OperationProvider')
  return context
}

function OperationDock() {
  const { operations, dismissOperation } = useOperations()
  const [expanded, setExpanded] = useState(true)
  if (!operations.length) return null
  const activeCount = operations.filter((item) => ['queued', 'running'].includes(item.progress.status)).length
  return (
    <aside className={`operation-dock ${expanded ? 'operation-dock--expanded' : ''}`} aria-label="Operations" aria-live="polite">
      <button className="operation-dock__toggle" type="button" onClick={() => setExpanded((value) => !value)} aria-expanded={expanded}>
        <span>{activeCount ? `${activeCount} operation${activeCount === 1 ? '' : 's'} running` : 'Recent operations'}</span>
        <ChevronDown aria-hidden="true" size={17} />
      </button>
      {expanded && <div className="operation-dock__list">
        {operations.map(({ accepted, label, progress }) => {
          const complete = ['succeeded', 'failed', 'cancelled'].includes(progress.status)
          const failed = progress.status === 'failed'
          return (
            <article className="operation-item" key={accepted.operationId} data-status={progress.status}>
              <span className="operation-item__icon" aria-hidden="true">
                {failed ? <CircleAlert size={17} /> : complete ? <Check size={17} /> : <LoaderCircle className="spin" size={17} />}
              </span>
              <div className="operation-item__body">
                <div className="operation-item__heading">
                  <strong>{label}</strong>
                  <span>{progress.percent ?? 0}%</span>
                </div>
                <p>{progress.message}</p>
                <div className="progress" role="progressbar" aria-label={`${label} progress`} aria-valuenow={progress.percent ?? 0} aria-valuemin={0} aria-valuemax={100}>
                  <span style={{ width: `${progress.percent ?? 0}%` }} />
                </div>
              </div>
              {complete && <button className="icon-button icon-button--small" type="button" aria-label={`Dismiss ${label}`} onClick={() => dismissOperation(accepted.operationId)}><X aria-hidden="true" size={15} /></button>}
            </article>
          )
        })}
      </div>}
    </aside>
  )
}
