import axe from 'axe-core'
import { screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { ApiError } from '../api/client'
import { createTestApi, renderApp } from '../test/renderApp'

describe('overview', () => {
  it('renders system state, capacity, the combined project grid, and the log dropdown', async () => {
    renderApp()

    expect(await screen.findByText('Your projects are ready when you are.')).toBeVisible()
    expect(screen.getByText('2 applications running · 1 update available')).toBeVisible()
    expect(screen.getByRole('heading', { name: 'System' })).toBeVisible()
    expect(screen.getByRole('meter', { name: 'CPU usage' })).toHaveAttribute('aria-valuenow', '18')
    expect(screen.getByRole('link', { name: 'View Studio' })).toBeVisible()
    expect(screen.getAllByRole('link', { name: 'Manage hosting' })).toHaveLength(2)
    expect(screen.getByText('Desktop Client')).toBeVisible()
    expect(screen.getAllByText('Planned')).not.toHaveLength(0)
    expect(screen.getByRole('heading', { name: 'Deployments & activity' })).toBeVisible()
    expect(screen.getByText('1 failed')).toBeVisible()
    expect(screen.getAllByText('Build exited with code 1. The previous deployment was preserved.')).toHaveLength(1)
  })

  it('offers a bulk deploy for outdated projects instead of demo actions', async () => {
    renderApp()

    expect(await screen.findByRole('button', { name: 'Deploy 1 update' })).toBeEnabled()
    expect(screen.queryByRole('button', { name: 'Start showcase' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Stop all demos' })).not.toBeInTheDocument()
  })

  it('has no automated accessibility violations in the primary view', async () => {
    const { container } = renderApp()
    await screen.findByText('Your projects are ready when you are.')
    const result = await axe.run(container, { rules: { 'color-contrast': { enabled: false } } })
    expect(result.violations).toEqual([])
  })

  it('shows an actionable service error and can retry', async () => {
    const api = createTestApi({ getOverview: () => Promise.reject(new ApiError('Abode to Web is unreachable. Check that the Windows service is running.')) })
    renderApp('/', api)

    expect(await screen.findByRole('heading', { name: 'Abode to Web is offline' })).toBeVisible()
    expect(screen.getByRole('button', { name: 'Try again' })).toBeEnabled()
  })

  it('explains that applications continue when the browser goes offline', async () => {
    renderApp()
    await screen.findByText('Your projects are ready when you are.')
    window.dispatchEvent(new Event('offline'))
    expect(await screen.findByText(/Existing applications continue running under the Abode to Web service/)).toBeVisible()
  })
})
