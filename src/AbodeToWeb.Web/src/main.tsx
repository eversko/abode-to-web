import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { BrowserRouter } from 'react-router'
import { ApiProvider } from './api/context'
import { AppRoutes } from './App'
import { OperationProvider } from './operations/OperationContext'
import './styles.css'

const root = document.getElementById('app')
if (!root) throw new Error('Abode to Web application root was not found.')

createRoot(root).render(
  <StrictMode>
    <BrowserRouter>
      <ApiProvider>
        <OperationProvider>
          <AppRoutes />
        </OperationProvider>
      </ApiProvider>
    </BrowserRouter>
  </StrictMode>,
)
