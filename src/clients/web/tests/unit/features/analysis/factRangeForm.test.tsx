import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { FactRangeForm } from '../../../../src/features/analysis/FactRangeForm';
import '../../../../src/i18n';

describe('FactRangeForm', () => {
  it('prefills the fields from the current range', () => {
    render(<FactRangeForm range={{ fromMs: 750_500, toMs: 900_000 }} onChange={vi.fn()} />);
    expect(screen.getByLabelText('From')).toHaveValue('12:30.500');
    expect(screen.getByLabelText('To')).toHaveValue('15:00');
  });

  it('starts empty without a range', () => {
    render(<FactRangeForm range={{}} onChange={vi.fn()} />);
    expect(screen.getByLabelText('From')).toHaveValue('');
    expect(screen.getByLabelText('To')).toHaveValue('');
  });

  it('reports the validated range on Apply', async () => {
    const onChange = vi.fn();
    render(<FactRangeForm range={{}} onChange={onChange} />);
    await userEvent.type(screen.getByLabelText('From'), '1:00');
    await userEvent.type(screen.getByLabelText('To'), '2:30.250');
    await userEvent.click(screen.getByRole('button', { name: 'Apply range' }));
    expect(onChange).toHaveBeenCalledWith({ fromMs: 60_000, toMs: 150_250 });
  });

  it('accepts an open-ended range', async () => {
    const onChange = vi.fn();
    render(<FactRangeForm range={{}} onChange={onChange} />);
    await userEvent.type(screen.getByLabelText('To'), '2:00');
    await userEvent.click(screen.getByRole('button', { name: 'Apply range' }));
    expect(onChange).toHaveBeenCalledWith({ toMs: 120_000 });
  });

  it('ties an invalid time to its field and reports nothing', async () => {
    const onChange = vi.fn();
    render(<FactRangeForm range={{}} onChange={onChange} />);
    await userEvent.type(screen.getByLabelText('From'), 'abc');
    await userEvent.click(screen.getByRole('button', { name: 'Apply range' }));
    const from = screen.getByLabelText('From');
    expect(from).toHaveAttribute('aria-invalid', 'true');
    expect(from).toHaveAccessibleDescription('Enter a time as m:ss or m:ss.SSS.');
    expect(screen.getByLabelText('To')).not.toHaveAttribute('aria-invalid', 'true');
    expect(onChange).not.toHaveBeenCalled();
  });

  it('rejects negative times', async () => {
    const onChange = vi.fn();
    render(<FactRangeForm range={{}} onChange={onChange} />);
    await userEvent.type(screen.getByLabelText('To'), '-1:00');
    await userEvent.click(screen.getByRole('button', { name: 'Apply range' }));
    expect(screen.getByLabelText('To')).toHaveAccessibleDescription('The time must not be negative.');
    expect(onChange).not.toHaveBeenCalled();
  });

  it('rejects an end that is not after the start', async () => {
    const onChange = vi.fn();
    render(<FactRangeForm range={{}} onChange={onChange} />);
    await userEvent.type(screen.getByLabelText('From'), '2:00');
    await userEvent.type(screen.getByLabelText('To'), '2:00');
    await userEvent.click(screen.getByRole('button', { name: 'Apply range' }));
    expect(screen.getByLabelText('To')).toHaveAccessibleDescription('The end must be after the start.');
    expect(onChange).not.toHaveBeenCalled();
  });

  it('clears the fields and reports an empty range', async () => {
    const onChange = vi.fn();
    render(<FactRangeForm range={{ fromMs: 60_000, toMs: 120_000 }} onChange={onChange} />);
    await userEvent.click(screen.getByRole('button', { name: 'Clear range' }));
    expect(screen.getByLabelText('From')).toHaveValue('');
    expect(screen.getByLabelText('To')).toHaveValue('');
    expect(onChange).toHaveBeenCalledWith({});
  });

  it('follows a range that changes from outside', () => {
    const { rerender } = render(<FactRangeForm range={{ fromMs: 60_000 }} onChange={vi.fn()} />);
    rerender(<FactRangeForm range={{ fromMs: 90_000, toMs: 180_000 }} onChange={vi.fn()} />);
    expect(screen.getByLabelText('From')).toHaveValue('1:30');
    expect(screen.getByLabelText('To')).toHaveValue('3:00');
  });
});
