"use client";

import { InputHTMLAttributes, ReactNode, TextareaHTMLAttributes, useState } from "react";
import { Eye, EyeOff, LucideIcon } from "lucide-react";
import clsx from "clsx";

interface SharedProps {
  label?: string;
  icon?: LucideIcon;
  containerClassName?: string;
  rightElement?: ReactNode;
  pill?: boolean;
  secureToggle?: boolean;
}

type InputProps = SharedProps & { multiline?: false } & Omit<InputHTMLAttributes<HTMLInputElement>, "className">;
type TextareaProps = SharedProps & { multiline: true; rows?: number } & Omit<
    TextareaHTMLAttributes<HTMLTextAreaElement>,
    "className"
  >;
type Props = InputProps | TextareaProps;

// Labeled text input with an optional leading icon and a thicker primary-colored border on
// focus, matching the SignUp mockup's input treatment. Pass multiline to render a <textarea>
// instead (e.g. for diagnoses/medications/treatment-plan fields), sharing the same chrome.
//
// Renders as a discriminated union on `multiline` rather than a single Omit<InputHTMLAttributes>
// type: input/textarea have incompatible onChange/onFocus event types, so the two field kinds
// need to be narrowed and destructured separately to keep `rest` type-safe for each element.
export default function TextField(props: Props) {
  const [focused, setFocused] = useState(false);
  const [hidden, setHidden] = useState(true);
  const { label, icon: Icon, containerClassName, rightElement, pill = false, secureToggle = false } = props;

  const fieldClassName =
    "flex-1 py-3 text-body-lg text-[15px] text-onSurface bg-transparent outline-none placeholder:text-outline resize-none";

  let field: ReactNode;
  if (props.multiline) {
    const { rows = 3, ...domProps } = stripShared(props);
    field = (
      <textarea
        rows={rows}
        className={fieldClassName}
        {...domProps}
        onFocus={(e) => {
          setFocused(true);
          domProps.onFocus?.(e);
        }}
        onBlur={(e) => {
          setFocused(false);
          domProps.onBlur?.(e);
        }}
      />
    );
  } else {
    const domProps = stripShared(props);
    field = (
      <input
        type={secureToggle ? (hidden ? "password" : "text") : domProps.type}
        className={fieldClassName}
        {...domProps}
        onFocus={(e) => {
          setFocused(true);
          domProps.onFocus?.(e);
        }}
        onBlur={(e) => {
          setFocused(false);
          domProps.onBlur?.(e);
        }}
      />
    );
  }

  return (
    <div className={containerClassName}>
      {label && <label className="block text-label-sm text-onSurfaceVariant mb-1.5">{label}</label>}
      <div
        className={clsx(
          "flex gap-2 bg-surfaceContainerLow border px-3.5",
          props.multiline ? "items-start py-3" : "items-center",
          pill ? "rounded-radii-full" : "rounded-radii-md",
          focused ? "border-[1.5px] border-primary" : "border-outlineVariant"
        )}
      >
        {Icon && <Icon size={20} className={clsx(focused ? "text-primary" : "text-outline", props.multiline && "mt-0.5")} />}
        {field}
        {secureToggle ? (
          <button type="button" onClick={() => setHidden((h) => !h)} className="text-outline">
            {hidden ? <Eye size={20} /> : <EyeOff size={20} />}
          </button>
        ) : (
          rightElement
        )}
      </div>
    </div>
  );
}

// Drops the component's own props, leaving only what's safe to spread onto the underlying
// <input>/<textarea> element.
function stripShared<T extends InputProps | TextareaProps>(
  props: T
): Omit<T, keyof SharedProps | "multiline"> {
  const { label, icon, containerClassName, rightElement, pill, secureToggle, multiline, ...rest } = props;
  void label;
  void icon;
  void containerClassName;
  void rightElement;
  void pill;
  void secureToggle;
  void multiline;
  return rest;
}
