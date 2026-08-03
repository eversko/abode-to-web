import { createContext, useContext, useMemo, type PropsWithChildren } from 'react'
import { HttpAbodeToWebApiClient, type AbodeToWebApiClient } from './client'
import { MockAbodeToWebApiClient } from './mock'

const ApiContext = createContext<AbodeToWebApiClient | null>(null)

const createDefaultClient = () => import.meta.env.DEV && import.meta.env.VITE_API_MODE !== 'http'
  ? new MockAbodeToWebApiClient()
  : new HttpAbodeToWebApiClient()

export function ApiProvider({ children, client }: PropsWithChildren<{ client?: AbodeToWebApiClient }>) {
  const value = useMemo(() => client ?? createDefaultClient(), [client])
  return <ApiContext.Provider value={value}>{children}</ApiContext.Provider>
}

export function useApi() {
  const client = useContext(ApiContext)
  if (!client) throw new Error('useApi must be used inside ApiProvider')
  return client
}
