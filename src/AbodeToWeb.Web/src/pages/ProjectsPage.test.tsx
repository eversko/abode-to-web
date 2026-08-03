import axe from 'axe-core'
import { screen, within } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { renderApp } from '../test/renderApp'

describe('projects', () => {
  it('lists Abode to Web projects, external sites, and local projects together', async () => {
    renderApp('/projects')

    expect(await screen.findByRole('heading', { level: 2, name: 'Your projects' })).toBeVisible()
    expect(screen.getByText('3 projects · 2 external sites · 4 local')).toBeVisible()
    expect(screen.getByRole('link', { name: 'View Studio' })).toBeVisible()
    expect(screen.getAllByText('Example Hosting')).toHaveLength(2)
    expect(screen.getAllByRole('link', { name: 'Open site' }).map((link) => link.getAttribute('href'))).toContain('https://status.example.com/')
    expect(screen.getAllByRole('link', { name: 'Manage hosting' })).toHaveLength(2)
    expect(screen.getByText('Desktop Client')).toBeVisible()
    expect(screen.getByText(/Planned → planned\.example\.com/)).toBeVisible()
    expect(screen.getByText('The repository folder was not found on disk.')).toBeVisible()
    expect(screen.getByRole('button', { name: 'Copy path for Desktop Client' })).toBeVisible()
  })

  it('keeps external site cards monitoring-only', async () => {
    renderApp('/projects')
    await screen.findByRole('heading', { level: 2, name: 'Your projects' })
    const card = screen.getAllByRole('link', { name: 'Manage hosting' })[0].closest('article')
    if (!card) throw new Error('external site card not found')
    expect(within(card).queryByRole('button', { name: 'Deploy' })).not.toBeInTheDocument()
    expect(within(card).queryByRole('button', { name: 'Start' })).not.toBeInTheDocument()
  })

  it('redirects the retired external-sites page to projects', async () => {
    renderApp('/external-sites')
    expect(await screen.findByRole('heading', { level: 2, name: 'Your projects' })).toBeVisible()
  })

  it('has no automated accessibility violations', async () => {
    const { container } = renderApp('/projects')
    await screen.findByRole('heading', { level: 2, name: 'Your projects' })
    const result = await axe.run(container, { rules: { 'color-contrast': { enabled: false } } })
    expect(result.violations).toEqual([])
  })
})
