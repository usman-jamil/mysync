import { useCopy } from './hooks';
import { CheckIcon, CopyIcon } from './icons';

interface CopyButtonProps {
  text: string;
  label: string;
  className?: string;
}

export function CopyButton({ text, label, className }: CopyButtonProps) {
  const [copied, copy] = useCopy();
  return (
    <button
      type="button"
      className={className ? `icon-button ${className}` : 'icon-button'}
      onClick={() => copy(text)}
      aria-label={copied ? 'Copied' : label}
      title={copied ? 'Copied' : label}
    >
      {copied ? <CheckIcon /> : <CopyIcon />}
    </button>
  );
}
