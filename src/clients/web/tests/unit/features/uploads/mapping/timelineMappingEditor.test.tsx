import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { vi } from 'vitest';
import { TimelineMappingEditor } from '../../../../../src/features/uploads/TimelineMappingEditor';
import { createDefaultDraft, type SpanDraft } from '../../../../../src/features/uploads/mappingDraft';
import { PlatformFailure } from '../../../../../src/platform/failure';
import '../../../../../src/i18n';

function Harness({ duration, failure, onChange }: { duration?: number; failure?: PlatformFailure; onChange?: (s: SpanDraft[]) => void }) {
  const [spans, setSpans] = useState(createDefaultDraft(duration));
  return (
    <TimelineMappingEditor
      spans={spans}
      durationSeconds={duration}
      showErrors
      failure={failure}
      onChange={(s) => {
        setSpans(s);
        onChange?.(s);
      }}
    />
  );
}

describe('TimelineMappingEditor', () => {
  it('shows the default span over the duration and accepts the match time', async () => {
    const onChange = vi.fn();
    render(<Harness duration={2700} onChange={onChange} />);
    expect(screen.getByLabelText('Media end (Span 1)')).toHaveValue('45:00');
    expect(screen.getByText('The recording is 45:00 long.')).toBeInTheDocument();
    expect(screen.getByText('This time is required.')).toBeInTheDocument();
    await userEvent.type(screen.getByLabelText('Match time at media start (Span 1)'), '10:00');
    expect(onChange).toHaveBeenLastCalledWith([{ mediaStart: '00:00', mediaEnd: '45:00', matchStart: '10:00' }]);
    expect(screen.queryByText('This time is required.')).not.toBeInTheDocument();
  });

  it('asks for the media end when no duration is known', () => {
    render(<Harness />);
    expect(screen.getByText(/length could not be read/)).toBeInTheDocument();
    expect(screen.getByLabelText('Media end (Span 1)')).toHaveValue('');
  });

  it('adds and removes spans but keeps the last one', async () => {
    render(<Harness duration={100} />);
    expect(screen.queryByRole('button', { name: /Remove span/ })).not.toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Add span' }));
    expect(screen.getByLabelText('Media start (Span 2)')).toHaveValue('01:40');
    await userEvent.click(screen.getByRole('button', { name: 'Remove span 1' }));
    expect(screen.queryByLabelText('Media start (Span 2)')).not.toBeInTheDocument();
    expect(screen.getByLabelText('Media start (Span 1)')).toHaveValue('01:40');
  });

  it('shows invalid input and the platform refusal next to the affected span', async () => {
    const failure = new PlatformFailure('validation', { fieldErrors: [{ field: 'timelineMapping.spans[1]', code: 'timeline-mapping-invalid' }] });
    render(<Harness duration={100} failure={failure} />);
    await userEvent.type(screen.getByLabelText('Match time at media start (Span 1)'), 'abc');
    expect(screen.getByText('Enter a time as mm:ss or hh:mm:ss.')).toBeInTheDocument();
    await userEvent.click(screen.getByRole('button', { name: 'Add span' }));
    expect(screen.getByText('The platform refused this span.')).toBeInTheDocument();
    expect(screen.getAllByText('The platform refused this span.')).toHaveLength(1);
    expect(screen.getByLabelText('Media start (Span 2)').closest('fieldset')).toHaveTextContent('The platform refused this span.');
  });

  it('shows a mapping-level platform refusal', () => {
    const failure = new PlatformFailure('validation', { fieldErrors: [{ field: 'timelineMapping', code: 'timeline-mapping-invalid' }] });
    render(<Harness duration={100} failure={failure} />);
    expect(screen.getByRole('alert')).toHaveTextContent('The platform refused this mapping.');
  });
});
