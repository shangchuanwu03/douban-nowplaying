using System;
using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DoubanNowPlaying
{
    /// <summary>
    /// 一套克制的动效工具：时长、缓动、透明度/位移统一从这里取。
    /// 系统关掉「在 Windows 中显示动画」时自动退化为直接切换。
    /// </summary>
    internal static class Anim
    {
        public const int Fast = 140;
        public const int Normal = 220;
        public const int Slow = 320;

        public static bool Enabled
        {
            get
            {
                try { return SystemParameters.ClientAreaAnimation; }
                catch { return true; }
            }
        }

        public static IEasingFunction EaseOut()
        {
            return new CubicEase { EasingMode = EasingMode.EaseOut };
        }

        public static IEasingFunction BackOut()
        {
            return new BackEase { Amplitude = 0.32, EasingMode = EasingMode.EaseOut };
        }

        /* ----------------------------- 属性动画 ----------------------------- */

        public static void Animate(DependencyObject target, DependencyProperty prop,
            double from, double to, int ms, int delay = 0, IEasingFunction ease = null)
        {
            if (target == null || prop == null) return;
            var animatable = target as IAnimatable;
            if (!Enabled || animatable == null)
            {
                if (animatable != null) animatable.BeginAnimation(prop, null);
                target.SetValue(prop, to);
                return;
            }

            var anim = new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = new Duration(TimeSpan.FromMilliseconds(Math.Max(1, ms))),
                BeginTime = TimeSpan.FromMilliseconds(Math.Max(0, delay)),
                EasingFunction = ease ?? EaseOut(),
                FillBehavior = FillBehavior.HoldEnd
            };
            animatable.BeginAnimation(prop, anim);
        }

        /// <summary>匀速（进度条用：跟播放时间走，不要缓动）</summary>
        public static void AnimateLinear(DependencyObject target, DependencyProperty prop,
            double from, double to, int ms)
        {
            if (target == null || prop == null) return;
            var animatable = target as IAnimatable;
            if (!Enabled || animatable == null)
            {
                if (animatable != null) animatable.BeginAnimation(prop, null);
                target.SetValue(prop, to);
                return;
            }
            var anim = new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = new Duration(TimeSpan.FromMilliseconds(Math.Max(1, ms))),
                FillBehavior = FillBehavior.HoldEnd
            };
            animatable.BeginAnimation(prop, anim);
        }

        public static void ColorTo(SolidColorBrush brush, Color to, int ms)
        {
            if (brush == null) return;
            if (brush.IsFrozen) return;
            if (!Enabled)
            {
                brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
                brush.Color = to;
                return;
            }
            var anim = new ColorAnimation(brush.Color, to, new Duration(TimeSpan.FromMilliseconds(Math.Max(1, ms))))
            {
                FillBehavior = FillBehavior.HoldEnd,
                EasingFunction = EaseOut()
            };
            brush.BeginAnimation(SolidColorBrush.ColorProperty, anim);
        }

        public static void FadeIn(UIElement el, int ms = Normal, int delay = 0, double from = 0)
        {
            if (el == null) return;
            el.Opacity = from;
            Animate(el, UIElement.OpacityProperty, from, 1, ms, delay);
        }

        /// <summary>淡出；结束后把透明度复位，方便下次再用</summary>
        public static void FadeOut(UIElement el, int ms = Normal, Action done = null)
        {
            if (el == null) return;
            if (!Enabled)
            {
                el.Opacity = 1;
                if (done != null) done();
                return;
            }
            var anim = new DoubleAnimation(el.Opacity, 0, new Duration(TimeSpan.FromMilliseconds(Math.Max(1, ms))))
            {
                FillBehavior = FillBehavior.HoldEnd,
                EasingFunction = EaseOut()
            };
            anim.Completed += (s, e) =>
            {
                el.BeginAnimation(UIElement.OpacityProperty, null);
                el.Opacity = 1;
                if (done != null) done();
            };
            el.BeginAnimation(UIElement.OpacityProperty, anim);
        }

        /* ----------------------------- 位移 / 缩放 ----------------------------- */

        static TransformGroup Group(FrameworkElement el)
        {
            var group = el.RenderTransform as TransformGroup;
            if (group == null)
            {
                group = new TransformGroup();
                if (el.RenderTransform != null) group.Children.Add(el.RenderTransform);
                el.RenderTransform = group;
                el.RenderTransformOrigin = new Point(0.5, 0.5);
            }
            return group;
        }

        public static TranslateTransform Translate(FrameworkElement el)
        {
            var group = Group(el);
            foreach (var t in group.Children)
            {
                var tt = t as TranslateTransform;
                if (tt != null) return tt;
            }
            var nt = new TranslateTransform();
            group.Children.Add(nt);
            return nt;
        }

        public static ScaleTransform Scale(FrameworkElement el)
        {
            var group = Group(el);
            foreach (var t in group.Children)
            {
                var st = t as ScaleTransform;
                if (st != null) return st;
            }
            var ns = new ScaleTransform(1, 1);
            group.Children.Add(ns);
            return ns;
        }

        public static RotateTransform Rotate(FrameworkElement el)
        {
            var group = Group(el);
            foreach (var t in group.Children)
            {
                var rt = t as RotateTransform;
                if (rt != null) return rt;
            }
            var nr = new RotateTransform(0);
            group.Children.Add(nr);
            return nr;
        }

        /// <summary>淡入 + 从下方轻轻抬起</summary>
        public static void SlideIn(FrameworkElement el, double fromY = 8, int ms = Normal, int delay = 0)
        {
            if (el == null) return;
            if (!Enabled)
            {
                el.Opacity = 1;
                var t0 = Translate(el);
                t0.Y = 0;
                return;
            }
            el.Opacity = 0;
            var t = Translate(el);
            t.Y = fromY;
            Animate(t, TranslateTransform.YProperty, fromY, 0, ms, delay, EaseOut());
            Animate(el, UIElement.OpacityProperty, 0, 1, ms, delay, EaseOut());
        }

        public static void SlideX(FrameworkElement el, double fromX, double toX, int ms, int delay = 0)
        {
            if (el == null) return;
            var t = Translate(el);
            if (!Enabled)
            {
                t.X = toX;
                return;
            }
            Animate(t, TranslateTransform.XProperty, fromX, toX, ms, delay, EaseOut());
        }

        public static void Pop(FrameworkElement el, double from, double to, int ms, int delay = 0)
        {
            if (el == null) return;
            var s = Scale(el);
            if (!Enabled)
            {
                s.ScaleX = to;
                s.ScaleY = to;
                return;
            }
            Animate(s, ScaleTransform.ScaleXProperty, from, to, ms, delay, BackOut());
            Animate(s, ScaleTransform.ScaleYProperty, from, to, ms, delay, BackOut());
        }

        public static void ScaleTo(FrameworkElement el, double from, double to, int ms, int delay = 0, bool back = false)
        {
            if (el == null) return;
            var s = Scale(el);
            if (!Enabled)
            {
                s.ScaleX = to;
                s.ScaleY = to;
                return;
            }
            var ease = back ? BackOut() : EaseOut();
            Animate(s, ScaleTransform.ScaleXProperty, from, to, ms, delay, ease);
            Animate(s, ScaleTransform.ScaleYProperty, from, to, ms, delay, ease);
        }

        /* ----------------------------- 循环动效 ----------------------------- */

        public static void PulseForever(FrameworkElement el, double scaleTo, double opacityTo, int ms)
        {
            if (el == null) return;
            var s = Scale(el);
            if (!Enabled)
            {
                s.ScaleX = 1;
                s.ScaleY = 1;
                el.Opacity = 1;
                return;
            }
            var grow = new DoubleAnimation(1, scaleTo, new Duration(TimeSpan.FromMilliseconds(ms)))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            s.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            s.BeginAnimation(ScaleTransform.ScaleYProperty, (DoubleAnimation)grow.Clone());

            var fade = new DoubleAnimation(1, opacityTo, new Duration(TimeSpan.FromMilliseconds(ms)))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            el.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        public static void RippleForever(FrameworkElement el, double scaleTo, int ms)
        {
            if (el == null) return;
            var s = Scale(el);
            if (!Enabled)
            {
                s.ScaleX = 1;
                s.ScaleY = 1;
                el.Opacity = 0;
                return;
            }
            var grow = new DoubleAnimation(1, scaleTo, new Duration(TimeSpan.FromMilliseconds(ms)))
            {
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut }
            };
            s.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            s.BeginAnimation(ScaleTransform.ScaleYProperty, (DoubleAnimation)grow.Clone());
            el.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0.6, 0, new Duration(TimeSpan.FromMilliseconds(ms)))
                {
                    RepeatBehavior = RepeatBehavior.Forever
                });
        }

        public static void RotateForever(FrameworkElement el, int ms)
        {
            if (el == null) return;
            var rt = Rotate(el);
            if (!Enabled) return;
            rt.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, new Duration(TimeSpan.FromMilliseconds(ms)))
                {
                    RepeatBehavior = RepeatBehavior.Forever
                });
        }

        public static void StopAnim(FrameworkElement el)
        {
            if (el == null) return;
            el.BeginAnimation(UIElement.OpacityProperty, null);
            el.Opacity = 1;
            var group = el.RenderTransform as TransformGroup;
            if (group == null) return;
            foreach (var t in group.Children)
            {
                var s = t as ScaleTransform;
                if (s != null)
                {
                    s.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                    s.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                    s.ScaleX = 1;
                    s.ScaleY = 1;
                }
                var r = t as RotateTransform;
                if (r != null)
                {
                    r.BeginAnimation(RotateTransform.AngleProperty, null);
                    r.Angle = 0;
                }
            }
        }

        public static void StopRotate(FrameworkElement el)
        {
            if (el == null) return;
            var group = el.RenderTransform as TransformGroup;
            if (group == null) return;
            foreach (var t in group.Children)
            {
                var r = t as RotateTransform;
                if (r != null)
                {
                    r.BeginAnimation(RotateTransform.AngleProperty, null);
                    r.Angle = 0;
                }
            }
        }

        /* ----------------------------- 交互动效 ----------------------------- */

        /// <summary>悬停轻微抬起、按下缩回。点起来有「实体按钮」的手感。</summary>
        public static void Pressable(FrameworkElement el, double hover = 1.03)
        {
            if (el == null) return;
            bool over = false;
            el.MouseEnter += (s, e) =>
            {
                over = true;
                if (!el.IsEnabled) return;
                var st = Scale(el);
                Animate(st, ScaleTransform.ScaleXProperty, st.ScaleX, hover, Fast, 0, EaseOut());
                Animate(st, ScaleTransform.ScaleYProperty, st.ScaleY, hover, Fast, 0, EaseOut());
            };
            el.MouseLeave += (s, e) =>
            {
                over = false;
                var st = Scale(el);
                Animate(st, ScaleTransform.ScaleXProperty, st.ScaleX, 1, Fast, 0, EaseOut());
                Animate(st, ScaleTransform.ScaleYProperty, st.ScaleY, 1, Fast, 0, EaseOut());
            };
            el.PreviewMouseDown += (s, e) =>
            {
                if (!el.IsEnabled) return;
                var st = Scale(el);
                Animate(st, ScaleTransform.ScaleXProperty, st.ScaleX, 0.96, 90, 0, EaseOut());
                Animate(st, ScaleTransform.ScaleYProperty, st.ScaleY, 0.96, 90, 0, EaseOut());
            };
            el.PreviewMouseUp += (s, e) =>
            {
                if (!el.IsEnabled) return;
                var st = Scale(el);
                double to = over ? hover : 1;
                Animate(st, ScaleTransform.ScaleXProperty, st.ScaleX, to, 150, 0, BackOut());
                Animate(st, ScaleTransform.ScaleYProperty, st.ScaleY, to, 150, 0, BackOut());
            };
        }

        /// <summary>列表逐行出现：只动前几行，后面的一次显示，避免长列表卡顿。</summary>
        public static void Stagger(Panel panel, int max = 10, double fromY = 10)
        {
            if (panel == null) return;
            int count = panel.Children.Count;
            int animate = Math.Min(count, max);
            for (int i = 0; i < count; i++)
            {
                var fe = panel.Children[i] as FrameworkElement;
                if (fe == null) continue;
                if (i < animate)
                {
                    SlideIn(fe, fromY, 260, Math.Min(i * 22, 220));
                }
                else
                {
                    fe.Opacity = 1;
                    var t = Translate(fe);
                    t.Y = 0;
                }
            }
        }

        /* ----------------------------- 数字滚动 ----------------------------- */

        public static void CountTo(TextBlock tb, double from, double to, string format, int ms)
        {
            if (tb == null) return;
            if (!Enabled)
            {
                tb.Text = to.ToString(format, CultureInfo.InvariantCulture);
                return;
            }
            var token = new object();
            tb.Tag = token;
            var sw = Stopwatch.StartNew();
            var timer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(16)
            };
            timer.Tick += (s, e) =>
            {
                if (!ReferenceEquals(tb.Tag, token))
                {
                    timer.Stop();
                    return;
                }
                double p = Math.Min(1, sw.Elapsed.TotalMilliseconds / Math.Max(1, ms));
                double eased = 1 - Math.Pow(1 - p, 3);
                tb.Text = (from + (to - from) * eased).ToString(format, CultureInfo.InvariantCulture);
                if (p >= 1)
                {
                    timer.Stop();
                    tb.Text = to.ToString(format, CultureInfo.InvariantCulture);
                }
            };
            timer.Start();
        }

        /* ----------------------------- 径向柔光 ----------------------------- */

        public static void DriftGlow(System.Windows.Shapes.Rectangle rect,
            double dx, double dy, int ms)
        {
            if (rect == null) return;
            var brush = rect.Fill as RadialGradientBrush;
            if (brush == null) return;
            if (brush.IsFrozen)
            {
                brush = brush.Clone();
                rect.Fill = brush;
            }
            if (!Enabled) return;

            var cx = brush.Center.X;
            var cy = brush.Center.Y;
            var anim = new PointAnimation(new Point(cx, cy), new Point(cx + dx, cy + dy),
                new Duration(TimeSpan.FromMilliseconds(ms)))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            brush.BeginAnimation(RadialGradientBrush.CenterProperty, anim);
            brush.BeginAnimation(RadialGradientBrush.GradientOriginProperty, (PointAnimation)anim.Clone());

            var fade = new DoubleAnimation(0.55, 1, new Duration(TimeSpan.FromMilliseconds(ms / 2.5)))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            rect.BeginAnimation(UIElement.OpacityProperty, fade);
        }
    }
}
