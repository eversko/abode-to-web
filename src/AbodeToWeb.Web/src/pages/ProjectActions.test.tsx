import { screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import type { OperationAccepted, OperationProgress } from '../api/schema'
import { createTestApi, renderApp } from '../test/renderApp'

const accepted: OperationAccepted = { operationId: 'operation-1', kind: 'deploy', progressUrl: '/operations/operation-1' }

describe('project actions and operation progress', () => {
  it('deploys only a reviewed remote commit and offers no working-folder publish action', async () => {
    const runProjectAction = vi.fn().mockResolvedValue(accepted)
    const api = createTestApi({
      runProjectAction,
      watchOperation: (_operation, handlers) => {
        handlers.onMessage({ operationId: accepted.operationId, kind: 'deploy', status: 'succeeded', stage: 'Complete', percent: 100, message: 'Deployed.', timestamp: new Date().toISOString() })
        return () => undefined
      },
    })
    renderApp('/projects/studio', api)

    await userEvent.click(await screen.findByRole('button', { name: 'Deploy' }))

    expect(runProjectAction).toHaveBeenCalledWith('studio', 'deploy')
    expect(screen.queryByRole('button', { name: /publish folder/i })).not.toBeInTheDocument()
    expect(screen.queryByText(/commit every change/i)).not.toBeInTheDocument()
  })

  it('submits only the predefined start action for a stopped project', async () => {
    const runProjectAction = vi.fn().mockResolvedValue({ ...accepted, kind: 'start' })
    const api = createTestApi({
      runProjectAction,
      watchOperation: (_operation, handlers) => {
        handlers.onMessage({ operationId: 'operation-1', kind: 'start', status: 'succeeded', stage: 'Complete', percent: 100, message: 'Started.', timestamp: new Date().toISOString() })
        return () => undefined
      },
    })
    renderApp('/projects', api)
    const docsLink = await screen.findByRole('link', { name: 'View Docs' })
    const docsCard = docsLink.closest<HTMLElement>('article')
    if (!docsCard) throw new Error('Docs card was not rendered.')
    await userEvent.click(within(docsCard).getByRole('button', { name: 'Start' }))

    expect(runProjectAction).toHaveBeenCalledWith('docs', 'start')
    expect(await screen.findByText('Started.')).toBeVisible()
  })

  it('shows asynchronous deployment stage and percentage without blocking the page', async () => {
    const progress: OperationProgress = {
      operationId: accepted.operationId,
      kind: 'deploy',
      status: 'running',
      stage: 'Building candidate',
      percent: 62,
      message: 'Building candidate',
      timestamp: new Date().toISOString(),
    }
    const api = createTestApi({
      runProjectAction: vi.fn().mockResolvedValue(accepted),
      watchOperation: (_operation, handlers) => {
        handlers.onMessage(progress)
        return () => undefined
      },
    })
    renderApp('/projects', api)
    const studioCard = (await screen.findByRole('link', { name: 'View Studio' })).closest<HTMLElement>('article')
    if (!studioCard) throw new Error('Studio card was not rendered.')
    await userEvent.click(within(studioCard).getByRole('button', { name: 'Deploy' }))

    expect(await screen.findByText('Building candidate')).toBeVisible()
    expect(screen.getByRole('progressbar', { name: 'Deploy Studio progress' })).toHaveAttribute('aria-valuenow', '62')
    expect(screen.getByRole('heading', { name: 'Your projects' })).toBeVisible()
  })
})
