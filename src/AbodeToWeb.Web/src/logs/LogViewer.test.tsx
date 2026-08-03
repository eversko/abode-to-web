import { screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import type { LogStreamOptions, StreamHandlers } from '../api/client'
import type { LogEntry } from '../api/schema'
import { createTestApi, renderApp } from '../test/renderApp'

describe('live logs', () => {
  it('streams entries, filters them, and allows auto-scroll to pause', async () => {
    const dispose = vi.fn()
    const streamLogs = vi.fn((_projectId: string | undefined, _options: LogStreamOptions, handlers: StreamHandlers<LogEntry>) => {
      handlers.onOpen?.()
      handlers.onMessage({ sequence: 1, timestamp: new Date().toISOString(), source: 'runtime', severity: 'information', message: 'Application ready on reserved port', projectId: 'studio' })
      handlers.onMessage({ sequence: 2, timestamp: new Date().toISOString(), source: 'runtime', severity: 'error', message: 'Application failed after startup', projectId: 'studio' })
      return dispose
    })
    const api = createTestApi({ streamLogs })
    renderApp('/projects/studio/logs', api)

    expect(await screen.findByText('Application ready on reserved port')).toBeVisible()
    expect(screen.getByRole('status', { name: '' })).toHaveTextContent('Live')
    const pause = screen.getByRole('button', { name: 'Pause scroll' })
    await userEvent.click(pause)
    expect(screen.getByRole('button', { name: 'Resume scroll' })).toHaveAttribute('aria-pressed', 'false')

    await userEvent.selectOptions(screen.getByRole('combobox', { name: 'Severity' }), 'error')
    expect(screen.queryByText('Application ready on reserved port')).not.toBeInTheDocument()
    expect(screen.getAllByText('Application failed after startup').length).toBeGreaterThan(0)

    await userEvent.type(screen.getByRole('searchbox', { name: 'Search logs' }), 'missing')
    expect(screen.getByText('Waiting for matching log entries…')).toBeVisible()
    expect(streamLogs).toHaveBeenCalled()
  })

  it('shows bounded system history and continues with live system events', async () => {
    const streamLogs = vi.fn((_projectId: string | undefined, _options: LogStreamOptions, handlers: StreamHandlers<LogEntry>) => {
      handlers.onOpen?.()
      handlers.onMessage({ sequence: 8, timestamp: new Date().toISOString(), source: 'system', severity: 'warning', message: 'Abode to Web service recovered after restart', projectId: null })
      return () => undefined
    })
    renderApp('/system', createTestApi({ streamLogs }))

    expect(await screen.findByRole('heading', { name: 'Abode to Web system logs' })).toBeVisible()
    expect(await screen.findByText('Abode to Web service recovered after restart')).toBeVisible()
    expect(streamLogs).toHaveBeenCalledWith(undefined, expect.objectContaining({ sources: ['system'], minimumSeverity: 'information' }), expect.any(Object))
    expect(screen.getByRole('searchbox', { name: 'Search logs' })).toBeVisible()
    expect(screen.getByRole('button', { name: 'Pause scroll' })).toBeVisible()
  })
})
