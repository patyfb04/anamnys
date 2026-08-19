import { HTMLAttributes, ReactNode } from "react";
import clsx from "clsx";

interface Props extends Omit<HTMLAttributes<HTMLDivElement>, "className"> {
  onClick?: () => void;
  className?: string;
  padded?: boolean;
  children: ReactNode;
}

// Flat tonal surface with a hairline border, matching the mockups' card treatment (white
// background, rounded-xl corners, no heavy shadow).
export default function Card({ onClick, className, padded = true, children, ...rest }: Props) {
  const classes = clsx(
    "bg-surfaceContainerLowest rounded-radii-lg border border-outlineVariant",
    padded && "p-4",
    onClick && "cursor-pointer hover:border-primary/50 transition-colors",
    className
  );

  if (onClick) {
    return (
      <div role="button" tabIndex={0} onClick={onClick} className={classes} {...rest}>
        {children}
      </div>
    );
  }

  return (
    <div className={classes} {...rest}>
      {children}
    </div>
  );
}
