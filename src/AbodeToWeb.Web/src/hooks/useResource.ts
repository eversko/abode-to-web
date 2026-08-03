import { useCallback, useEffect, useState } from 'react'

export type ResourceState<T> = {
  data?: T
  error?: Error
  loading: boolean
  reload: () => void
}

export function useResource<T>(loader: (signal: AbortSignal) => Promise<T>): ResourceState<T> {
  const [data, setData] = useState<T>()
  const [error, setError] = useState<Error>()
  const [loading, setLoading] = useState(true)
  const [version, setVersion] = useState(0)

  useEffect(() => {
    const controller = new AbortController()
    setLoading(true)
    setError(undefined)
    loader(controller.signal)
      .then((result) => {
        if (!controller.signal.aborted) setData(result)
      })
      .catch((reason: unknown) => {
        if (!controller.signal.aborted) setError(reason instanceof Error ? reason : new Error('Something went wrong.'))
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false)
      })
    return () => controller.abort()
  }, [loader, version])

  const reload = useCallback(() => setVersion((current) => current + 1), [])
  return { data, error, loading, reload }
}
