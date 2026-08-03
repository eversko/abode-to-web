import { act, screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { createTestApi, renderApp } from '../test/renderApp'
import type { StreamHandlers } from '../api/client'
import type { OperationProgress } from '../api/schema'

const openManualSetup = () => userEvent.click(screen.getByRole('button', { name: 'Set up manually in dashboard' }))
const agentAssessPrompt = 'Read https://github.com/eversko/abode-to-web/blob/main/docs/AGENT-QUICKSTART.md and follow it for this repository. Start with ASSESS only: inspect read-only, report whether the project fits Abode to Web, and show the exact plan. Do not install, elevate, edit files, handle secrets, deploy, publish, or change DNS, firewall, router, or power settings. Stop and wait for my explicit APPROVE LOCAL SETUP message. After local verification, stop again and wait for my separate APPROVE PUBLISH message.'

describe('project onboarding', () => {
  it('starts with a one-click provider-neutral agent handoff', async () => {
    const api = createTestApi()
    const writeText = vi.fn().mockResolvedValue(undefined)
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText } })
    renderApp('/projects/new', api)

    expect(screen.getByRole('heading', { name: 'Give this to your LLM' })).toBeVisible()
    expect(screen.getByRole('link', { name: 'Open agent guide' })).toHaveAttribute(
      'href',
      'https://github.com/eversko/abode-to-web/blob/main/docs/AGENT-QUICKSTART.md',
    )
    expect(screen.getByRole('button', { name: 'Set up manually in dashboard' })).toBeVisible()
    expect(screen.queryByRole('textbox', { name: 'Repository path' })).not.toBeInTheDocument()
    expect(screen.getByText(agentAssessPrompt, { selector: 'code' })).toBeInTheDocument()

    const gates = screen.getByRole('list', { name: 'Approval gates' })
    expect(within(gates).getAllByRole('listitem')).toHaveLength(3)
    expect(within(gates).getByText('ASSESS')).toBeVisible()
    expect(within(gates).getByText('APPROVE LOCAL SETUP')).toBeVisible()
    expect(within(gates).getByText('APPROVE PUBLISH')).toBeVisible()

    await userEvent.click(screen.getByRole('button', { name: 'Copy prompt' }))
    expect(writeText).toHaveBeenCalledWith(agentAssessPrompt)
    expect(await screen.findByRole('status')).toHaveTextContent('Prompt copied')
  })

  it('announces clipboard failures and keeps a manual path available', async () => {
    const api = createTestApi()
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText: vi.fn().mockRejectedValue(new Error('denied')) } })
    renderApp('/projects/new', api)

    await userEvent.click(screen.getByRole('button', { name: 'Copy prompt' }))
    expect(await screen.findByRole('alert')).toHaveTextContent('Could not copy automatically')
    expect(screen.getByRole('link', { name: 'Open agent guide' })).toBeVisible()
  })

  it('defaults to a disabled, save-only project', async () => {
    const api = createTestApi()
    const validateProject = vi.spyOn(api, 'validateProject').mockResolvedValue({ isValid: true, issues: [] })
    renderApp('/projects/new', api)

    await openManualSetup()
    await userEvent.type(screen.getByRole('textbox', { name: 'Repository path' }), 'C:/Projects/demo')
    await userEvent.click(screen.getByRole('button', { name: /Inspect repository/ }))
    await userEvent.click(await screen.findByRole('button', { name: /Configure project/ }))

    expect(screen.getByRole('combobox', { name: 'Visibility' })).toHaveValue('disabled')
    expect(screen.getByRole('checkbox', { name: 'Run a test build' })).not.toBeChecked()
    expect(screen.getByRole('textbox', { name: /Public hostname/ })).not.toBeRequired()

    await userEvent.click(screen.getByRole('button', { name: /Validate configuration/ }))
    expect(validateProject).toHaveBeenCalledWith(expect.objectContaining({
      installSuggestionId: null,
      buildSuggestionId: null,
      startSuggestionId: null,
      runTestBuild: false,
      visibility: 'disabled',
    }))
    expect(await screen.findByText(/Nothing will execute or be published/)).toBeVisible()
    expect(screen.getByText('None — stable static path')).toBeVisible()
    expect(screen.getByRole('button', { name: /Save project/ })).toBeVisible()
  })

  it('reviews server-issued command suggestions without silently selecting them', async () => {
    const api = createTestApi()
    const detectProject = vi.spyOn(api, 'detectProject')
    renderApp('/projects/new', api)
    await openManualSetup()
    await userEvent.type(screen.getByRole('textbox', { name: 'Repository path' }), 'C:/Projects/demo')
    await userEvent.click(screen.getByRole('button', { name: /Inspect repository/ }))

    expect(await screen.findByRole('heading', { name: 'Review detection' })).toBeVisible()
    expect(detectProject).toHaveBeenCalledWith({ kind: 'existing', path: 'C:/Projects/demo' })
    const suggestions = screen.getAllByRole('checkbox')
    expect(suggestions).toHaveLength(3)
    suggestions.forEach((suggestion) => expect(suggestion).not.toBeChecked())
    expect(screen.getByText('npm ci')).toBeVisible()
    expect(screen.getByText(/one Windows trust domain/)).toBeVisible()
  })

  it('requires and submits the Cloudflare Access audience for protected projects', async () => {
    const api = createTestApi()
    const validateProject = vi.spyOn(api, 'validateProject')
    renderApp('/projects/new', api)

    await openManualSetup()
    await userEvent.type(screen.getByRole('textbox', { name: 'Repository path' }), 'C:/Projects/demo')
    await userEvent.click(screen.getByRole('button', { name: /Inspect repository/ }))
    await userEvent.click(await screen.findByRole('button', { name: /Configure project/ }))
    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Visibility' }), 'protected')

    const audience = screen.getByRole('textbox', { name: 'Cloudflare Access application audience' })
    expect(audience).toBeRequired()
    await userEvent.type(screen.getByRole('textbox', { name: /Public hostname/ }), 'demo.example.com')
    await userEvent.type(audience, 'cloudflare-access-audience')
    await userEvent.click(screen.getByRole('button', { name: /Validate configuration/ }))

    expect(validateProject).toHaveBeenCalledWith(expect.objectContaining({
      visibility: 'protected',
      accessApplicationAudience: 'cloudflare-access-audience',
    }))
    expect(await screen.findByText('Configuration is valid')).toBeVisible()
  })

  it('shows detected repository and runtime settings for explicit review and submits edits', async () => {
    const api = createTestApi()
    const validateProject = vi.spyOn(api, 'validateProject')
    renderApp('/projects/new', api)

    await openManualSetup()
    await userEvent.type(screen.getByRole('textbox', { name: 'Repository path' }), 'C:/Projects/demo')
    await userEvent.click(screen.getByRole('button', { name: /Inspect repository/ }))
    expect(await screen.findByText('Provider suggestion')).toBeVisible()
    expect(screen.getByText('Current branch')).toBeVisible()
    await userEvent.click(screen.getByRole('button', { name: /Configure project/ }))

    expect(screen.getByRole('combobox', { name: 'Repository provider' })).toHaveValue('github')
    expect(screen.getByRole('textbox', { name: 'Git remote name' })).toHaveValue('origin')
    expect(screen.getByRole('textbox', { name: 'Deployment branch' })).toHaveValue('main')
    expect(screen.getByRole('textbox', { name: 'Committed static output directory' })).toHaveValue('dist')
    expect(screen.getByRole('textbox', { name: 'Port environment variable' })).toHaveValue('PORT')

    await userEvent.clear(screen.getByRole('textbox', { name: 'Deployment branch' }))
    await userEvent.type(screen.getByRole('textbox', { name: 'Deployment branch' }), 'release')
    await userEvent.clear(screen.getByRole('textbox', { name: 'Committed static output directory' }))
    await userEvent.type(screen.getByRole('textbox', { name: 'Committed static output directory' }), 'public-build')
    await userEvent.type(screen.getByRole('textbox', { name: /Public hostname/ }), 'demo.example.com')
    await userEvent.click(screen.getByRole('button', { name: /Validate configuration/ }))

    expect(validateProject).toHaveBeenCalledWith(expect.objectContaining({
      repositoryProvider: 'github',
      repositoryRemote: 'origin',
      repositoryBranch: 'release',
      staticOutputDirectory: 'public-build',
      portEnvironmentVariable: 'PORT',
    }))
  })

  it('surfaces the clone destination and inspects it only after clone success', async () => {
    const api = createTestApi()
    const cloneProject = vi.spyOn(api, 'cloneProject')
    const detectProject = vi.spyOn(api, 'detectProject')
    let operationHandlers: StreamHandlers<OperationProgress> | undefined
    vi.spyOn(api, 'watchOperation').mockImplementation((_operation, handlers) => {
      operationHandlers = handlers
      return () => undefined
    })
    renderApp('/projects/new', api)

    await openManualSetup()
    await userEvent.click(screen.getByRole('radio', { name: /Clone from GitHub/ }))
    await userEvent.type(screen.getByRole('textbox', { name: 'GitHub repository' }), 'owner/demo')
    await userEvent.click(screen.getByRole('button', { name: /Clone repository/ }))

    expect(cloneProject).toHaveBeenCalledWith('owner/demo')
    expect(await screen.findByText(/Clone accepted for/)).toHaveTextContent('C:/Projects/demo')
    expect(detectProject).not.toHaveBeenCalled()

    act(() => operationHandlers?.onMessage({
      operationId: 'clone-operation', kind: 'test-build', status: 'succeeded', stage: 'Complete',
      percent: 100, message: 'Clone complete.', timestamp: new Date().toISOString(),
    }))
    expect(await screen.findByRole('heading', { name: 'Review detection' })).toBeVisible()
    expect(detectProject).toHaveBeenCalledWith({ kind: 'existing', path: 'C:/Projects/demo' })
  })

  it('describes project creation as accepted until the operation succeeds', async () => {
    const api = createTestApi()
    let operationHandlers: StreamHandlers<OperationProgress> | undefined
    vi.spyOn(api, 'watchOperation').mockImplementation((_operation, handlers) => {
      operationHandlers = handlers
      return () => undefined
    })
    renderApp('/projects/new', api)

    await openManualSetup()
    await userEvent.type(screen.getByRole('textbox', { name: 'Repository path' }), 'C:/Projects/demo')
    await userEvent.click(screen.getByRole('button', { name: /Inspect repository/ }))
    await userEvent.click(await screen.findByRole('button', { name: /Configure project/ }))
    await userEvent.type(screen.getByRole('textbox', { name: /Public hostname/ }), 'demo.example.com')
    await userEvent.click(screen.getByRole('checkbox', { name: 'Run a test build' }))
    await userEvent.click(screen.getByRole('button', { name: /Validate configuration/ }))
    await userEvent.click(await screen.findByRole('button', { name: /Save and test/ }))

    expect(await screen.findByRole('heading', { name: 'demo was accepted' })).toBeVisible()
    expect(screen.getByText(/accepted the request/)).toBeVisible()
    act(() => operationHandlers?.onMessage({
      operationId: 'add-operation', kind: 'test-build', status: 'succeeded', stage: 'Complete',
      percent: 100, message: 'Project added.', timestamp: new Date().toISOString(),
    }))
    expect(await screen.findByRole('heading', { name: 'demo was added' })).toBeVisible()
  })

  it('replaces the in-progress project state with actionable failure details', async () => {
    const api = createTestApi()
    let operationHandlers: StreamHandlers<OperationProgress> | undefined
    vi.spyOn(api, 'watchOperation').mockImplementation((_operation, handlers) => {
      operationHandlers = handlers
      return () => undefined
    })
    renderApp('/projects/new', api)

    await openManualSetup()
    await userEvent.type(screen.getByRole('textbox', { name: 'Repository path' }), 'C:/Projects/demo')
    await userEvent.click(screen.getByRole('button', { name: /Inspect repository/ }))
    await userEvent.click(await screen.findByRole('button', { name: /Configure project/ }))
    await userEvent.type(screen.getByRole('textbox', { name: /Public hostname/ }), 'demo.example.com')
    await userEvent.click(screen.getByRole('button', { name: /Validate configuration/ }))
    await userEvent.click(await screen.findByRole('button', { name: /Save project/ }))
    expect(await screen.findByRole('heading', { name: 'demo was accepted' })).toBeVisible()

    act(() => operationHandlers?.onMessage({
      operationId: 'add-operation', kind: 'test-build', status: 'failed', stage: 'Build failed',
      percent: 60, message: 'The candidate build failed. The previous release was not changed.',
      errorCode: 'build.command_failed', timestamp: new Date().toISOString(),
    }))
    expect(await screen.findByRole('heading', { name: 'demo setup did not complete' })).toBeVisible()
    expect(screen.getAllByText(/candidate build failed/)).not.toHaveLength(0)
    expect(screen.getByRole('link', { name: /Review project/ })).toHaveAttribute('href', '/projects/demo')
  })

  it('keeps clone failures on the editable source step', async () => {
    const api = createTestApi()
    let operationHandlers: StreamHandlers<OperationProgress> | undefined
    vi.spyOn(api, 'watchOperation').mockImplementation((_operation, handlers) => {
      operationHandlers = handlers
      return () => undefined
    })
    renderApp('/projects/new', api)

    await openManualSetup()
    await userEvent.click(screen.getByRole('radio', { name: /Clone from GitHub/ }))
    await userEvent.type(screen.getByRole('textbox', { name: 'GitHub repository' }), 'owner/missing')
    await userEvent.click(screen.getByRole('button', { name: /Clone repository/ }))
    expect(await screen.findByText(/Clone accepted for/)).toBeVisible()

    act(() => operationHandlers?.onMessage({
      operationId: 'clone-operation', kind: 'test-build', status: 'failed', stage: 'Clone failed',
      percent: 10, message: 'GitHub CLI could not clone the repository.', errorCode: 'github.clone_failed',
      timestamp: new Date().toISOString(),
    }))
    expect(await screen.findByRole('alert')).toHaveTextContent('GitHub CLI could not clone the repository.')
    expect(screen.getByRole('textbox', { name: 'GitHub repository' })).toHaveValue('owner/missing')
    expect(screen.queryByText(/Clone accepted for/)).not.toBeInTheDocument()
  })
})
