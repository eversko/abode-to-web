import { screen, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { createTestApi, renderApp } from '../test/renderApp'

describe('dangerous action confirmations', () => {
  it('requires explicit confirmation before rollback', async () => {
    const rollback = vi.fn().mockResolvedValue({ operationId: 'rollback-1', kind: 'rollback', progressUrl: '/operations/rollback-1' })
    const api = createTestApi({ rollback, watchOperation: () => () => undefined })
    renderApp('/projects/studio/deployments', api)

    const trigger = await screen.findByRole('button', { name: 'Rollback' })
    await userEvent.click(trigger)
    const dialog = screen.getByRole('alertdialog', { name: 'Roll back to 01f8d22?' })
    expect(dialog).toBeVisible()
    expect(rollback).not.toHaveBeenCalled()

    await userEvent.click(screen.getByRole('button', { name: 'Roll back' }))
    expect(rollback).toHaveBeenCalledWith('studio', 'dep-studio-a')
  })

  it('closes with Escape and restores focus to the rollback trigger', async () => {
    const user = userEvent.setup()
    renderApp('/projects/studio/deployments')
    const trigger = await screen.findByRole('button', { name: 'Rollback' })
    await user.click(trigger)

    expect(screen.getByRole('button', { name: 'Cancel' })).toHaveFocus()
    await user.keyboard('{Escape}')
    expect(screen.queryByRole('alertdialog')).not.toBeInTheDocument()
    expect(trigger).toHaveFocus()
  })

  it('confirms project removal without offering to delete the repository', async () => {
    const deleteProject = vi.fn().mockResolvedValue({ operationId: 'delete-1', kind: 'validate-configuration', progressUrl: '/operations/delete-1' })
    const api = createTestApi({ deleteProject, watchOperation: () => () => undefined })
    renderApp('/projects/studio/settings', api)
    await userEvent.click(await screen.findByRole('button', { name: 'Delete project' }))

    const dialog = screen.getByRole('alertdialog', { name: 'Delete Studio?' })
    expect(dialog).toBeVisible()
    expect(within(dialog).getByText(/deletes its Abode to Web-managed secrets/i)).toBeVisible()
    expect(within(dialog).getByText(/permanently retires its project ID/i)).toBeVisible()
    expect(within(dialog).getByText(/source repository and immutable deployment and audit history remain/i)).toBeVisible()
    expect(screen.queryByRole('checkbox', { name: 'Also remove the local repository' })).not.toBeInTheDocument()
    await userEvent.click(within(dialog).getByRole('button', { name: 'Delete project' }))
    expect(deleteProject).toHaveBeenCalledWith('studio', false)
  })
})
