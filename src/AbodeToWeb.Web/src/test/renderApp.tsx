import { render } from '@testing-library/react'
import { MemoryRouter } from 'react-router'
import { AppRoutes } from '../App'
import type { AbodeToWebApiClient } from '../api/client'
import { ApiProvider } from '../api/context'
import { MockAbodeToWebApiClient } from '../api/mock'
import { OperationProvider } from '../operations/OperationContext'

export const createTestApi = (overrides: Partial<AbodeToWebApiClient> = {}): AbodeToWebApiClient => Object.assign(new MockAbodeToWebApiClient(), overrides)

export function renderApp(route = '/', client: AbodeToWebApiClient = createTestApi()) {
  return render(
    <MemoryRouter initialEntries={[route]}>
      <ApiProvider client={client}>
        <OperationProvider>
          <AppRoutes />
        </OperationProvider>
      </ApiProvider>
    </MemoryRouter>,
  )
}
