import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import type { GitStatus, OperationAccepted, ProjectDetail } from '../api/schema'
import { createTestApi, renderApp } from '../test/renderApp'

async function projectWithGit(overrides: Partial<GitStatus>): Promise<ProjectDetail> {
  const project = await createTestApi().getProject('studio')
  const baseline = project.summary.git
  if (!baseline) throw new Error('The Studio test fixture must include Git status.')
  return { ...project, summary: { ...project.summary, git: { ...baseline, ...overrides } } }
}

describe('project environment secrets', () => {
  it('offers a names-only environment example from the bounded project endpoint', async () => {
    const getEnvironmentExampleUrl = vi.fn().mockReturnValue('/api/v1/projects/studio/secrets/example')
    renderApp('/projects/studio/environment', createTestApi({ getEnvironmentExampleUrl }))

    const download = await screen.findByRole('link', { name: 'Download .env.example' })
    expect(getEnvironmentExampleUrl).toHaveBeenCalledWith('studio')
    expect(download).toHaveAttribute('href', '/api/v1/projects/studio/secrets/example')
    expect(download).toHaveAttribute('download', 'studio.env.example')
  })

  it('adds a new key through the protected secret API and clears its value from the UI', async () => {
    const replaceSecret = vi.fn().mockResolvedValue(undefined)
    const api = createTestApi({ replaceSecret })
    renderApp('/projects/studio/environment', api)

    await userEvent.type(await screen.findByRole('textbox', { name: 'Secret key' }), 'API_TOKEN')
    const value = screen.getByLabelText<HTMLInputElement>('Secret value')
    await userEvent.type(value, 'never-render-this-again')
    await userEvent.click(screen.getByRole('button', { name: 'Add secret' }))

    await waitFor(() => expect(replaceSecret).toHaveBeenCalledWith('studio', 'API_TOKEN', 'never-render-this-again'))
    expect(await screen.findByText('API_TOKEN was added.')).toBeVisible()
    expect(value).toHaveValue('')
    expect(screen.queryByText('never-render-this-again')).not.toBeInTheDocument()
    expect(screen.getAllByText(/••••••••••••/)).toHaveLength(2)
  })

  it('does not let the add form bypass confirmation for an existing key', async () => {
    const replaceSecret = vi.fn().mockResolvedValue(undefined)
    renderApp('/projects/studio/environment', createTestApi({ replaceSecret }))

    await userEvent.type(await screen.findByRole('textbox', { name: 'Secret key' }), 'DATABASE_URL')
    await userEvent.type(screen.getByLabelText('Secret value'), 'replacement')
    await userEvent.click(screen.getByRole('button', { name: 'Add secret' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('DATABASE_URL already exists. Use Replace')
    expect(replaceSecret).not.toHaveBeenCalled()
  })
})

describe('project Git status', () => {
  it.each([
    ['Not checked', { checkedAt: null, error: null, updateAvailable: false }],
    ['Check failed', { checkedAt: new Date().toISOString(), error: 'GitHub could not be reached.', updateAvailable: false }],
    ['Update available', { checkedAt: new Date().toISOString(), error: null, updateAvailable: true }],
    ['Up to date', { checkedAt: new Date().toISOString(), error: null, updateAvailable: false }],
  ] satisfies readonly (readonly [string, Partial<GitStatus>])[])('renders %s as a distinct state', async (label, git) => {
    const project = await projectWithGit(git)
    renderApp('/projects/studio', createTestApi({ getProject: vi.fn().mockResolvedValue(project) }))
    expect(await screen.findByText(label)).toBeVisible()
  })

  it('runs the predefined update check and refreshes after completion', async () => {
    const accepted: OperationAccepted = { operationId: 'git-check-1', kind: 'check-for-updates', progressUrl: '/operations/git-check-1' }
    const runProjectAction = vi.fn().mockResolvedValue(accepted)
    const getProject = vi.fn(createTestApi().getProject.bind(createTestApi()))
    const api = createTestApi({
      getProject,
      runProjectAction,
      watchOperation: (_operation, handlers) => {
        handlers.onMessage({ operationId: accepted.operationId, kind: accepted.kind, status: 'succeeded', stage: 'Complete', percent: 100, message: 'Repository checked.', timestamp: new Date().toISOString() })
        return () => undefined
      },
    })
    renderApp('/projects/studio', api)

    await userEvent.click(await screen.findByRole('button', { name: 'Check for updates' }))
    expect(runProjectAction).toHaveBeenCalledWith('studio', 'check-for-updates')
    expect(await screen.findByText('Repository checked.')).toBeVisible()
    await waitFor(() => expect(getProject).toHaveBeenCalledTimes(2))
  })
})

describe('project domain status', () => {
  it('reports the configured offline-page state instead of assuming it is enabled', async () => {
    const project = await createTestApi().getProject('studio')
    const configured: ProjectDetail = {
      ...project,
      desiredConfiguration: {
        ...project.desiredConfiguration,
        routing: { ...project.desiredConfiguration.routing, offlinePage: false },
      },
    }
    renderApp('/projects/studio/domain', createTestApi({ getProject: vi.fn().mockResolvedValue(configured) }))

    const label = await screen.findByText('Offline page')
    expect(label.nextElementSibling).toHaveTextContent('Disabled')
  })
})
